using System.IO;
using System.Text.Json;

namespace RamAI.Phase4.Services;

/// <summary>
/// Données persistées dans C:\ProgramData\RAM-AI\stats.json.
/// Toutes les propriétés ont une valeur par défaut sûre (jamais null / exception).
/// </summary>
public sealed class StatsData
{
    // ── Global ────────────────────────────────────────────────────────────────
    public DateTime FirstLaunch             { get; set; } = DateTime.UtcNow;
    public int      TotalSessions           { get; set; } = 0;
    public long     TotalUsageMinutes       { get; set; } = 0;
    public double   TotalRamFreedGb         { get; set; } = 0.0;
    public long     TotalProcessesOptimized { get; set; } = 0;

    // ── Meilleure session ─────────────────────────────────────────────────────
    public double   BestSessionRamFreedGb   { get; set; } = 0.0;
    public DateTime BestSessionDate         { get; set; } = DateTime.MinValue;

    // ── Stats du jour (réinitialisées si TodayDate ≠ aujourd'hui) ─────────────
    public string TodayDate                 { get; set; } = string.Empty;
    public double TodayRamFreedGb           { get; set; } = 0.0;
    public long   TodayProcessesOptimized   { get; set; } = 0;

    // ── Compteurs par mode — activations (transitions inactive→active) ─────────
    public int    GamingActivations         { get; set; } = 0;
    public double GamingRamFreedGb          { get; set; } = 0.0;

    public int    AiActivations             { get; set; } = 0;
    public double AiRamFreedGb              { get; set; } = 0.0;

    public int    BrowserActivations        { get; set; } = 0;
    public double BrowserRamFreedGb         { get; set; } = 0.0;

    public int    EcoActivations            { get; set; } = 0;
    public double EcoRamFreedGb             { get; set; } = 0.0;

    public int    TurboUseCount             { get; set; } = 0;
    public int    TournamentUseCount        { get; set; } = 0;
}

/// <summary>
/// Lecture / écriture de stats.json. Aucune exception ne remonte jamais.
/// </summary>
public sealed class StatsService
{
    private static readonly string SharedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAM-AI");

    public static readonly string StatsPath = Path.Combine(SharedDir, "stats.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>
    /// Charge stats.json.
    /// Si absent → retourne un objet vierge (FirstLaunch = maintenant).
    /// Si corrompu → supprime le fichier et retourne un objet vierge.
    /// </summary>
    public StatsData Load()
    {
        if (!File.Exists(StatsPath))
            return new StatsData();

        try
        {
            var data = JsonSerializer.Deserialize<StatsData>(File.ReadAllText(StatsPath), Opts);
            return data ?? new StatsData();
        }
        catch
        {
            try { File.Delete(StatsPath); } catch { }
            return new StatsData();
        }
    }

    /// <summary>Écrit stats.json sur disque. Aucune exception.</summary>
    public void Save(StatsData data)
    {
        try
        {
            Directory.CreateDirectory(SharedDir);
            File.WriteAllText(StatsPath, JsonSerializer.Serialize(data, Opts));
        }
        catch { }
    }
}
