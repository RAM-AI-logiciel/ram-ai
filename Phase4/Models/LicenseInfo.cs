namespace RamAI.Phase4.Models;

// LicenseTier.Starter supprimé — tiers valides : None, Pro, Ultra, Beta
public enum LicenseTier { None, Pro, Ultra, Beta }

public sealed class LicenseInfo
{
    public static readonly LicenseInfo Empty = new(LicenseTier.None, string.Empty);

    public LicenseTier Tier    { get; }
    public string      Key     { get; }
    public long        MaxCacheVirtualBytes { get; }

    public string TierLabel => Tier switch
    {
        LicenseTier.Pro   => "Pro",
        LicenseTier.Ultra => "Ultra",
        LicenseTier.Beta  => "Bêta testeur",
        _                 => "Aucune",
    };

    public string CacheLimitLabel => Tier switch
    {
        LicenseTier.Pro   => "256 Go virtuels",
        LicenseTier.Ultra => "Illimité",
        LicenseTier.Beta  => "256 Go virtuels (bêta)",
        _                 => "—",
    };

    /// <summary>True uniquement pour la licence Ultra.</summary>
    public bool IsUltra => Tier == LicenseTier.Ultra;

    public LicenseInfo(LicenseTier tier, string key)
    {
        Tier = tier;
        Key  = key;
        MaxCacheVirtualBytes = tier switch
        {
            LicenseTier.Pro   => 256L * 1024 * 1024 * 1024,
            LicenseTier.Ultra => long.MaxValue,
            LicenseTier.Beta  => 256L * 1024 * 1024 * 1024,
            _                 => 0,
        };
    }
}
