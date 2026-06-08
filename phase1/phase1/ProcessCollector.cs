using System.Diagnostics;
using System.Text.Json;

namespace RamAI.Phase1;

internal sealed class ProcessCollector
{
    // PID → page-fault count from previous sample (for delta)
    private readonly Dictionary<int, long> _prevPageFaults = [];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    internal List<ProcessSnapshot> Collect()
    {
        var results = new List<ProcessSnapshot>();
        var now = DateTime.UtcNow;

        foreach (Process proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    // Refresh to get current memory counters
                    proc.Refresh();

                    long ws      = proc.WorkingSet64;
                    long priv    = proc.PrivateMemorySize64;
                    long faults  = proc.PagedMemorySize64; // used as access-frequency proxy

                    _prevPageFaults.TryGetValue(proc.Id, out long prevFaults);
                    long delta = faults - prevFaults;
                    _prevPageFaults[proc.Id] = faults;

                    results.Add(new ProcessSnapshot
                    {
                        Timestamp       = now,
                        Pid             = proc.Id,
                        Name            = proc.ProcessName,
                        WorkingSetBytes = ws,
                        PrivateBytes    = priv,
                        PageFaultDelta  = delta < 0 ? 0 : delta,
                    });
                }
                catch
                {
                    // Protected / already-exited processes — skip silently
                }
            }
        }

        return results;
    }

    internal static void Persist(List<ProcessSnapshot> snapshots, string jsonPath)
    {
        const int MaxRows = 10_000;

        PatternStore store;
        if (File.Exists(jsonPath))
        {
            try
            {
                using var fs = File.OpenRead(jsonPath);
                store = JsonSerializer.Deserialize<PatternStore>(fs, JsonOpts) ?? new PatternStore();
            }
            catch { store = new PatternStore(); }
        }
        else
        {
            store = new PatternStore();
        }

        store.Snapshots.AddRange(snapshots);

        if (store.Snapshots.Count > MaxRows)
            store.Snapshots.RemoveRange(0, store.Snapshots.Count - MaxRows);

        store.LastUpdated = DateTime.UtcNow;

        string dir = Path.GetDirectoryName(jsonPath)!;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var wfs = File.Create(jsonPath);
        JsonSerializer.Serialize(wfs, store, JsonOpts);
    }
}
