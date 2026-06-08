using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;

namespace RamAI.Phase4.ViewModels;

public sealed partial class LicenseViewModel : ObservableObject
{
    private readonly LicenseService _licenseService;

    [ObservableProperty] private string _keyInput          = string.Empty;
    [ObservableProperty] private string _validationMessage = string.Empty;
    [ObservableProperty] private bool   _isValid;

    // ── Paliers affichés dans la fenêtre ──────────────────────────────────────
    public TierRow[] Tiers { get; } =
    [
        new("Pro",   "P-XXXX-XXXX",            "256 Go virtuels", "Optimisation ML avancée"),
        new("Ultra", "ULT-XXXX-XXXX-XXXX-XXXX","Illimité",        "Tournoi, profils jeux, prédictif"),
    ];

    public event Action? RequestClose;

    public LicenseViewModel(LicenseService licenseService)
    {
        _licenseService = licenseService;
        if (licenseService.Current.Tier != LicenseTier.None)
        {
            KeyInput          = licenseService.Current.Key;
            ValidationMessage = $"✓  Licence {licenseService.Current.TierLabel} active";
            IsValid           = true;
        }
    }

    [RelayCommand]
    private void ValidateKey()
    {
        var tier = _licenseService.Validate(KeyInput);
        if (tier != LicenseTier.None)
        {
            _licenseService.SaveLicense(KeyInput, tier);
            ValidationMessage = $"✓  Licence {tier} activée avec succès !";
            IsValid           = true;
            RequestClose?.Invoke();
        }
        else
        {
            string raw = KeyInput.Trim();
            ValidationMessage = string.IsNullOrEmpty(raw)
                ? "✗  Clé invalide ou expirée"
                : raw.StartsWith("BETA-", StringComparison.OrdinalIgnoreCase)
                    ? "✗  Clé BETA invalide ou expirée"
                    : raw.StartsWith("ULT-", StringComparison.OrdinalIgnoreCase)
                        ? "✗  Clé Ultra invalide — format : ULT-XXXX-XXXX-XXXX-XXXX"
                        : "✗  Formats acceptés : P-XXXX-XXXX  |  ULT-XXXX-XXXX-XXXX-XXXX  |  BETA-…";
            IsValid = false;
        }
    }

    [RelayCommand]
    private void ClearLicense()
    {
        _licenseService.Clear();
        KeyInput          = string.Empty;
        ValidationMessage = "Licence supprimée.";
        IsValid           = false;
    }
}

public sealed record TierRow(string Name, string Format, string CacheLimit, string Description);
