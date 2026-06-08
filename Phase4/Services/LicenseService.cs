using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RamAI.Phase4.Models;
using DPScope = System.Security.Cryptography.DataProtectionScope;

namespace RamAI.Phase4.Services;

/// <summary>
/// Validation HMAC/SHA-256 et persistance de la licence dans le registre Windows.
///
/// Emplacement registre : HKCU\Software\RAM-AI\License  (valeur "Key")
/// Activation bêta     : HKCU\Software\RAM-AI\License  (valeur "BetaActivation")
///
/// Format clé Pro      : P-{XXXX}-{XXXX}                 X ∈ [A-Z0-9]  SHA-256 % 251
/// Format clé Ultra    : ULT-{XXXX}-{XXXX}-{XXXX}-{CCCC} X ∈ [A-F0-9] HMAC-SHA256
/// Format clé Bêta     : BETA-{XXXX}-{XXXX}-{XXXX}-{CCCC} X ∈ [A-F0-9] HMAC-SHA256
///
/// Clés de démonstration :
///   Pro   : P-DEMO-0001
///   Ultra : ULT-DEMO-DEMO-DEMO-9999
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

    // Aucune clé demo — toute validation passe par HMAC/SHA-256.

    // ── État courant ──────────────────────────────────────────────────────────
    public event Action<LicenseInfo>? LicenseChanged;

    private LicenseInfo _current = LicenseInfo.Empty;
    public  LicenseInfo Current  => _current;

    public DateTime? BetaExpiryDate { get; private set; }

    // ── Validation ────────────────────────────────────────────────────────────

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

    // ── Validation BETA ───────────────────────────────────────────────────────
    //  Format : BETA-XXXX-XXXX-XXXX-CCCC
    //  CCCC = 2 premiers octets HMAC-SHA256(BetaSalt, XXXXXXXXXXXX)

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
    //  Format : ULT-XXXX-XXXX-XXXX-CCCC
    //  CCCC = 2 premiers octets HMAC-SHA256(UltSalt, XXXXXXXXXXXX)

    private static bool ValidateUltraKey(string key)
    {
        if (!Regex.IsMatch(key, @"^ULT-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$",
                           RegexOptions.IgnoreCase))
            return false;

        string[] parts    = key.Split('-');
        // parts : ["ULT", g1, g2, g3, checksum]
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

    // ── Persistance registre (clé chiffrée via Windows DPAPI) ────────────────
    //
    // La valeur stockée est Base64(DPAPI.Protect(UTF8(key), CurrentUser)).
    // Seul le compte Windows qui a chiffré peut déchiffrer — illisible pour
    // tout autre utilisateur ou toute autre machine.
    //
    // Migration silencieuse : si la valeur n'est pas du Base64-DPAPI valide
    // (ancienne installation), on tente de la valider comme clé brute, puis
    // on la re-chiffre immédiatement.

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

            // Tenter déchiffrement DPAPI
            string? plainKey = TryDecryptKey(stored);

            if (plainKey is null)
            {
                // Migration : valeur non chiffrée (ancienne installation)
                plainKey = stored.Trim().ToUpperInvariant();
                var migTier = Validate(plainKey);
                if (migTier == LicenseTier.None) return LicenseInfo.Empty;
                // Re-chiffrer silencieusement
                SaveLicense(plainKey, migTier);
                return _current;
            }

            var tier = Validate(plainKey);
            if (tier == LicenseTier.None) return LicenseInfo.Empty;

            _current = new LicenseInfo(tier, plainKey);

            if (tier == LicenseTier.Beta)
                IsBetaExpired();
        }
        catch { }

        return _current;
    }

    public void SaveLicense(string rawKey, LicenseTier tier)
    {
        string plain = rawKey.Trim().ToUpperInvariant();

        using var regKey = Registry.CurrentUser.CreateSubKey(RegPath, writable: true);
        regKey.SetValue(RegValue, EncryptKey(plain), RegistryValueKind.String);

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
            key?.DeleteValue(RegValue, throwOnMissingValue: false);
        }
        catch { }

        BetaExpiryDate = null;
        _current       = LicenseInfo.Empty;
        LicenseChanged?.Invoke(_current);
    }
}
