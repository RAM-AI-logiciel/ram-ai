using System.Text.Json;
using RamAI.Phase2.Data;

namespace RamAI.Phase2.ML;

/// <summary>
/// Loads patterns.json and converts it into supervised training rows.
///
/// Strategy
/// ────────
/// For every process PID we sort its snapshots by timestamp.
/// Row at index  t  becomes one training row whose:
///   • features = metrics at time  t
///   • label    = (pageFaultDelta at time t+1) > 0
///             i.e. "did the process access memory 2 s later?"
///
/// The last snapshot per PID has no future → it is discarded.
/// </summary>
internal static class DataLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static List<ProcessTrainingRow> Load(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"patterns.json not found at: {jsonPath}");

        // ── 1. Deserialise ────────────────────────────────────────────────────
        using var stream = File.OpenRead(jsonPath);
        var store = JsonSerializer.Deserialize<PatternStore>(stream, JsonOpts)
                    ?? throw new InvalidDataException("patterns.json is empty or malformed.");

        Console.WriteLine($"  [DataLoader] Raw snapshots loaded : {store.Snapshots.Count:N0}");

        // ── 2. Group by PID, sort by time ─────────────────────────────────────
        var byPid = store.Snapshots
            .GroupBy(s => s.Pid)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToList());

        // ── 3. Build (feature@t, label@t+1) pairs ────────────────────────────
        var rows = new List<ProcessTrainingRow>();

        foreach (var (_, timeline) in byPid)
        {
            for (int t = 0; t < timeline.Count - 1; t++)
            {
                var cur  = timeline[t];
                var next = timeline[t + 1];

                rows.Add(ToTrainingRow(cur, label: next.PageFaultDelta > 0));
            }
        }

        int positives = rows.Count(r => r.Label);
        Console.WriteLine($"  [DataLoader] Training rows built   : {rows.Count:N0}  " +
                          $"(active={positives:N0} / {100.0 * positives / rows.Count:F1}%)");

        return rows;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcessTrainingRow ToTrainingRow(RawSnapshot s, bool label)
    {
        float wsMB     = (float)(s.WorkingSetBytes  / (1024.0 * 1024.0));
        float privMB   = (float)(s.PrivateBytes     / (1024.0 * 1024.0));
        float pfKB     = (float)(Math.Min(s.PageFaultDelta, 100L * 1024 * 1024) / 1024.0);
        float ratio    = privMB > 0f ? wsMB / privMB : 1f;
        float logWS    = wsMB  > 0f ? (float)Math.Log2(wsMB) : 0f;

        return new ProcessTrainingRow
        {
            WorkingSetMB      = wsMB,
            PrivateBytesMB    = privMB,
            PageFaultDeltaKB  = pfKB,
            WsToPrivateRatio  = ratio,
            LogWorkingSet     = logWS,
            Label             = label,
        };
    }
}

// ── Internal JSON DTO ─────────────────────────────────────────────────────────

file sealed class PatternStore
{
    public DateTime           LastUpdated { get; set; }
    public List<RawSnapshot>  Snapshots   { get; set; } = [];
}
