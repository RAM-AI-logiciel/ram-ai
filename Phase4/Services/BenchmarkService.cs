using System.IO;
using System.Text;
using System.Text.Json;
using RamAI.Phase4.Models;

namespace RamAI.Phase4.Services;

public sealed class BenchmarkService
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RAM-AI", "benchmarks.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public List<BenchmarkSession> LoadHistory()
    {
        if (!File.Exists(DataFile)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<List<BenchmarkSession>>(
                             File.ReadAllText(DataFile), Opts);
            return result ?? new();
        }
        catch
        {
            // Fichier corrompu (crash en cours d'écriture) → le supprimer pour
            // ne pas bloquer tous les démarrages suivants.
            TryDeleteCorrupt(DataFile);
            return new();
        }
    }

    private static void TryDeleteCorrupt(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void SaveSession(BenchmarkSession session)
    {
        try
        {
            var list = LoadHistory();
            list.Add(session);
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(list, Opts));
        }
        catch { }
    }

    public void ExportCsv(IEnumerable<BenchmarkSession> sessions, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date;Mode actif;RAM libérée (Go);Processus optimisés;Durée;RAM totale (Go);RAM avant (Go);Temps moy. cycle (ms)");
        foreach (var s in sessions)
            sb.AppendLine(
                $"{s.StartTime:yyyy-MM-dd HH:mm:ss};{s.ActiveMode};" +
                $"{s.TotalRamFreedGb:F2};{s.ProcessesOptimized};" +
                $"{s.DurationLabel};{s.TotalRamGb:F1};" +
                $"{s.RamUsedBeforeGb:F2};{s.AvgCycleMs:F0}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
