using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RamAI.Phase4.Models;
using DPScope = System.Security.Cryptography.DataProtectionScope;

namespace RamAI.Phase4.Services;

/// <summary>
/// Validation de licence — deux chemins :
///
///   1. Clés locales (offline) — HMAC/SHA-256, pas de réseau :
///        Pro   : P-{XXXX}-{XXXX}                  SHA-256 % 251
///        Ultra : ULT-{XXXX}-{XXXX}-{XXXX}-{CCCC}  HMAC-SHA256
///        Bêta  : BETA-{XXXX}-{XXXX}-{XXXX}-{CCCC} HMAC-SHA256 (expire 30j)
///
///   2. Clés Lemon Squeezy (UUID) — validation en ligne :
///        Format : xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
///        API    : POST https://api.lemonsqueezy.com/v1/licenses/validate
///        Tier   : déduit de meta.product_name / meta.variant_name
///
/// Persistance : HKCU\Software\RAM-AI\License  →  Base64(DPAPI(clé))
/// </summary>
public sealed class LicenseService
{
    // ── Constantes ────────────────────────────────────────────────────────────

    private const string ProSalt     = "RAM-AI-2026";
    private const string BetaSalt    = "RAM-AI-BETA-2026-PRIVATE";
    private const string UltSalt     = "RAM-AI-ULT-2026-PRIVATE";
    private const string RegPath     = @"Software\RAM-AI\License";
    private const string RegValue    = "Key";
    private const string RegBetaDate = "BetaActivation";

    private const int BetaDurationDays = 30;

    private const string LsValidateUrl = "https://api.lemonsqueezy.com/v1/licenses/validate";

    private static readonly Regex _uuidRegex = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    // ── État courant ──────────────────────────────────────────────────────────
    public event Action<LicenseInfo>? LicenseChanged;

    private LicenseInfo _current = LicenseInfo.Empty;
    public  LicenseInfo Current  => _current;

    public DateTime? BetaExpiryDate { get; private set; }

    // ── Détection UUID ────────────────────────────────────────────────────────

    public static bool IsUuidKey(string rawKey) =>
        _uuidRegex.IsMatch(rawKey.Trim());

    // ── Validation locale (offline) ───────────────────────────────────────────

    public LicenseTier Validate(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return LicenseTier.None;

        string key = rawKey.Trim().ToUpperInvariant();

        // BETA-
        if (key.StartsWith("BETA-", StringComparison.Ordinal))
            return ValidateBetaKey(key) ? LicenseTier.Beta : LicenseTier.None;

        // ULT-  (Ultra — HMAC-SHA256)
        if (key.StartsWith("ULT-", StringComparison.Ordinal))
            return ValidateUltraKey(key) ? LicenseTier.Ultra : LicenseTier.None;

        // P-XXXX-XXXX  (Pro — SHA-256 % 251)
        if (!Regex.IsMatch(key, @"^P-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            return LicenseTier.None;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key + ProSalt));
        if ((hash[0] + hash[1] + hash[2]) % 251 != 0) return LicenseTier.None;

        return LicenseTier.Pro;
    }

    // ── Validation Lemon Squeezy (online) ────────────────────────────────────

    /// <summary>
    /// Valide une clé UUID auprès de l'API Lemon Squeezy.
    /// Retourne (None, null) si la clé n'est pas un UUID (ne lève pas d'exception).
    /// </summary>
    public async Task<LemonSqueezyResult> ValidateLemonSqueezyAsync(string rawKey)
    {
        string key = rawKey.Trim();

        if (!_uuidRegex.IsMatch(key))
            return new(LicenseTier.None, LsError.None);

        try
        {
            var body = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("license_key", key)
            ]);

            using var response = await _http.PostAsync(LsValidateUrl, body)
                                            .ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Trace.WriteLine($"[LicenseService] LS validate HTTP {(int)response.StatusCode} — {json}");

            JsonNode? node = JsonNode.Parse(json);

            bool   valid  = node?["valid"]?.GetValue<bool>()   ?? false;
            string status = node?["license_key"]?["status"]?.GetValue<string>() ?? "";

            Trace.WriteLine($"[LicenseService] valid={valid} status={status}");

            if (!valid)
                return new(LicenseTier.None, LsError.None);

            // Statuts acceptés : "active" (déjà activée) ou "inactive" (jamais activée — 1ère utilisation).
            // "expired" et "disabled" sont des refus légitimes.
            bool statusOk = string.Equals(status, "active",   StringComparison.OrdinalIgnoreCase)
                         || string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase);

            if (!statusOk)
            {
                Trace.WriteLine($"[LicenseService] Rejet : statut '{status}' non accepté");
                return new(LicenseTier.None, LsError.None);
            }

            // Déterminer le tier depuis le nom produit / variante
            string productName = node?["meta"]?["product_name"]?.GetValue<string>() ?? "";
            string variantName = node?["meta"]?["variant_name"]?.GetValue<string>() ?? "";

            Trace.WriteLine($"[LicenseService] product='{productName}' variant='{variantName}'");

            bool isUltra = productName.Contains("Ultra", StringComparison.OrdinalIgnoreCase)
                        || variantName.Contains("Ultra", StringComparison.OrdinalIgnoreCase);

            return new(isUltra ? LicenseTier.Ultra : LicenseTier.Pro, LsError.None);
        }
        catch (HttpRequestException ex)
        {
            Trace.WriteLine($"[LicenseService] Erreur réseau : {ex.Message}");
            return new(LicenseTier.None, LsError.Network);
        }
        catch (TaskCanceledException)
        {
            Trace.WriteLine("[LicenseService] Timeout");
            return new(LicenseTier.None, LsError.Network);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LicenseService] Erreur inattendue : {ex}");
            return new(LicenseTier.None, LsError.None);
        }
    }

    // ── Validation BETA ───────────────────────────────────────────────────────

    private static bool ValidateBetaKey(string key)
    {
        if (!Regex.IsMatch(key, @"^BETA-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$",
                           RegexOptions.IgnoreCase))
            return false;

        string[] parts    = key.Split('-');
        string   data     = parts[1] + parts[2] + parts[3];
        string   checksum = parts[4];

        byte[] hmac     = HMACSHA256.HashData(Encoding.UTF8.GetBytes(BetaSalt),
                                              Encoding.UTF8.GetBytes(data));
        string expected = $"{hmac[0]:X2}{hmac[1]:X2}";
        return checksum.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation ULTRA ──────────────────────────────────────────────────────

    private static bool ValidateUltraKey(string key)
    {
        if (!Regex.IsMatch(key, @"^ULT-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$",
                           RegexOptions.IgnoreCase))
            return false;

        string[] parts    = key.Split('-');
        string   data     = parts[1] + parts[2] + parts[3];
        string   checksum = parts[4];

        byte[] hmac     = HMACSHA256.HashData(Encoding.UTF8.GetBytes(UltSalt),
                                              Encoding.UTF8.GetBytes(data));
        string expected = $"{hmac[0]:X2}{hmac[1]:X2}";
        return checksum.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    // ── Vérification expiration bêta ──────────────────────────────────────────

    public bool IsBetaExpired()
    {
        DateTime activationDate = GetOrWriteBetaActivationDate();
        DateTime expiryDate     = activationDate.AddDays(BetaDurationDays);
        BetaExpiryDate = expiryDate;
        return DateTime.UtcNow > expiryDate;
    }

    private static DateTime GetOrWriteBetaActivationDate()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            string?  raw  = key?.GetValue(RegBetaDate) as string;
            if (!string.IsNullOrEmpty(raw) && long.TryParse(raw, out long ticks))
                return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch { }

        DateTime activation = DateTime.UtcNow;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath, writable: true);
            key.SetValue(RegBetaDate, activation.Ticks.ToString(), RegistryValueKind.String);
        }
        catch { }

        return activation;
    }

    // ── Persistance registre (DPAPI) ─────────────────────────────────────────

    private static string EncryptKey(string plainKey)
    {
        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainKey), null, DPScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? TryDecryptKey(string stored)
    {
        try
        {
            byte[] cipher = Convert.FromBase64String(stored);
            byte[] plain  = ProtectedData.Unprotect(cipher, null, DPScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public LicenseInfo LoadSaved()
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            if (regKey is null) return LicenseInfo.Empty;

            string? stored = regKey.GetValue(RegValue) as string;
            if (string.IsNullOrWhiteSpace(stored)) return LicenseInfo.Empty;

            string? plainKey = TryDecryptKey(stored);

            if (plainKey is null)
            {
                // Migration : valeur non chiffrée (ancienne installation)
                plainKey = stored.Trim().ToUpperInvariant();
                var migTier = Validate(plainKey);
                if (migTier == LicenseTier.None) return LicenseInfo.Empty;
                SaveLicense(plainKey, migTier);
                return _current;
            }

            LicenseTier tier;
            if (IsUuidKey(plainKey))
            {
                // Clé Lemon Squeezy : tier persisté séparément (pas de validation locale possible)
                string? tierRaw = regKey.GetValue("Tier") as string;
                if (!int.TryParse(tierRaw, out int tierInt) || tierInt <= 0)
                    return LicenseInfo.Empty;
                tier = (LicenseTier)tierInt;
            }
            else
            {
                tier = Validate(plainKey);
                if (tier == LicenseTier.None) return LicenseInfo.Empty;
            }

            _current = new LicenseInfo(tier, plainKey);

            if (tier == LicenseTier.Beta)
                IsBetaExpired();
        }
        catch { }

        return _current;
    }

    public void SaveLicense(string rawKey, LicenseTier tier)
    {
        // Les clés locales sont normalisées en majuscules ; les UUID conservent leur casse d'origine
        string plain = IsUuidKey(rawKey) ? rawKey.Trim() : rawKey.Trim().ToUpperInvariant();

        using var regKey = Registry.CurrentUser.CreateSubKey(RegPath, writable: true);
        regKey.SetValue(RegValue, EncryptKey(plain), RegistryValueKind.String);

        // Stocker le tier numérique pour les clés UUID (pas de validation locale possible)
        regKey.SetValue("Tier", ((int)tier).ToString(), RegistryValueKind.String);

        _current = new LicenseInfo(tier, plain);

        if (tier == LicenseTier.Beta)
            IsBetaExpired();

        LicenseChanged?.Invoke(_current);
    }

    public void Clear()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            key?.DeleteValue(RegValue,  throwOnMissingValue: false);
            key?.DeleteValue("Tier",    throwOnMissingValue: false);
        }
        catch { }

        BetaExpiryDate = null;
        _current       = LicenseInfo.Empty;
        LicenseChanged?.Invoke(_current);
    }
}

// ── Types résultat Lemon Squeezy ─────────────────────────────────────────────

public enum LsError { None, Network }

public readonly record struct LemonSqueezyResult(LicenseTier Tier, LsError Error);
