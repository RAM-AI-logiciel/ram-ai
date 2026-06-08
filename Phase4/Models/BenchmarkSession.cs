namespace RamAI.Phase4.Models;

public sealed class BenchmarkSession
{
    public DateTime StartTime          { get; set; }
    public DateTime EndTime            { get; set; }
    public double   TotalRamGb         { get; set; }
    public double   RamUsedBeforeGb    { get; set; }
    public double   TotalRamFreedGb    { get; set; }
    public int      ProcessesOptimized { get; set; }
    public double   AvgCycleMs         { get; set; }
    public string   ActiveMode         { get; set; } = string.Empty;
    public Dictionary<string, double> RamFreedByMode { get; set; } = new();

    public TimeSpan Duration      => EndTime - StartTime;
    public string   DurationLabel => $"{(int)Duration.TotalSeconds}s";
}
