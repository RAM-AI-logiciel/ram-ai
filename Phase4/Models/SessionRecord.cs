namespace RamAI.Phase4.Models;

public sealed class SessionRecord
{
    public DateTime StartTime          { get; set; }
    public long     DurationSeconds    { get; set; }
    public double   RamFreedGb         { get; set; }
    public long     ProcessesOptimized { get; set; }
    public string   ActiveMode         { get; set; } = string.Empty;
    public string   VramInfo           { get; set; } = string.Empty;

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}
