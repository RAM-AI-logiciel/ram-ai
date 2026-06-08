using System.IO;
using System.Text.Json;

namespace RamAI.Phase4.Services;

/// <summary>
/// Données persistées dans C:\ProgramData\RAM-AI\stats.json.
/// Toutes les propriétés ont des valeurs par défaut sûres.
/// </summary>
public sealed class StatsData
{
    public DateTime FirstLaunch               { get; set; } = DateTime.UtcNow;
    public int      TotalSessions             { get; set; } = 0;
    public double   TotalRamFreedGb           { get; set; } = 0.0;
    public long     TotalProcessesOptimized   { get; set; } = 0;

    // Stats du jour — réinitialisées si TodayDate ≠ aujourd'hui
    public string   TodayDate                 { get; set; } = string.Empty;
    public double   TodayRamFreedGb           { get; set; } = 0.0;
    public long     TodayProcessesOptimized   { get; set; } = 0;
}

/// <summary>
/// Lecture / écriture de stats.json.
/// Aucune exception ne peut remonter vers l'appelant.
/// </summary>
public sealed class StatsService
{
    private static readonly string SharedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAM-AI");

    public static readonly string StatsPath = Path.Combine(SharedDir, "stats.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>
    /// Charge stats.json. Si absent ou corrompu → crée un objet vierge
    /// (et supprime le fichier corrompu s'il existe).
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
            // Fichier corrompu → le supprimer pour garantir les démarrages suivants
            try { File.Delete(StatsPath); } catch { }
            return new StatsData();
        }
    }

    /// <summary>Persiste les stats sur disque. Aucune exception.</summary>
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
