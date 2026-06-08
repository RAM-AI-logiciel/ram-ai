using System.Text;
using RamAI.Phase1;

// ── Configuration ────────────────────────────────────────────────────────────

const int    SampleIntervalMs = 2_000;
const int    DisplayRows      = 25;        // top-N processes shown in the table
const string JsonPath         = @"data\patterns.json";

// ── Setup console ────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;
Console.CursorVisible  = false;
Console.Title          = "RAM-AI Phase 1 — Process Memory Monitor";

// Ensure the window is tall enough to avoid flicker on the status line
if (OperatingSystem.IsWindows())
{
    try { Console.WindowHeight = Math.Max(Console.WindowHeight, DisplayRows + 8); } catch { }
    try { Console.BufferHeight = Math.Max(Console.BufferHeight, DisplayRows + 8); } catch { }
}

// ── Ctrl-C handler ───────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Main loop ────────────────────────────────────────────────────────────────

var collector = new ProcessCollector();
long iteration = 0;

Console.Clear();

while (!cts.Token.IsCancellationRequested)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // 1. Collect
    List<ProcessSnapshot> snapshots = collector.Collect();

    // 2. Persist
    try   { ProcessCollector.Persist(snapshots, JsonPath); }
    catch { /* non-fatal — disk full, permissions, etc. */ }

    // 3. Render table
    Render(snapshots, ++iteration);

    // 4. Wait remainder of interval
    sw.Stop();
    int wait = SampleIntervalMs - (int)sw.ElapsedMilliseconds;
    if (wait > 0)
    {
        try { await Task.Delay(wait, cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}

Console.SetCursorPosition(0, DisplayRows + 6);
Console.CursorVisible = true;
Console.WriteLine("\nMonitor stopped.");

// ── Rendering ────────────────────────────────────────────────────────────────

static void Render(List<ProcessSnapshot> all, long iter)
{
    // Sort by Working Set descending, take top-N
    var top = all
        .OrderByDescending(p => p.WorkingSetBytes)
        .Take(DisplayRows)
        .ToList();

    var sb = new StringBuilder();

    // Header
    sb.AppendLine(
        $"  RAM-AI Phase 1  |  Iteration {iter,6}  |  " +
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  Processes: {all.Count,5}");
    sb.AppendLine(new string('─', 100));
    sb.AppendLine(
        $"  {"PID",7}  {"Name",-30}  {"WorkingSet",12}  {"PrivateBytes",12}  {"PgFault/s",10}  {"Bar",-20}");
    sb.AppendLine(new string('─', 100));

    foreach (var p in top)
    {
        string bar = MakeBar(p.WorkingSetBytes, GetMaxWS(top));
        sb.AppendLine(
            $"  {p.Pid,7}  {Truncate(p.Name, 30),-30}  " +
            $"{FormatBytes(p.WorkingSetBytes),12}  " +
            $"{FormatBytes(p.PrivateBytes),12}  " +
            $"{p.PageFaultDelta,10}  " +
            $"{bar,-20}");
    }

    // Pad remaining rows so old content is erased
    int printed = top.Count;
    for (int i = printed; i < DisplayRows; i++)
        sb.AppendLine(new string(' ', 100));

    sb.AppendLine(new string('─', 100));
    sb.Append($"  JSON → {JsonPath}   |  Press Ctrl+C to stop");

    Console.SetCursorPosition(0, 0);
    Console.Write(sb);
}

static long GetMaxWS(List<ProcessSnapshot> list) =>
    list.Count == 0 ? 1 : list.Max(p => p.WorkingSetBytes);

static string MakeBar(long value, long max)
{
    if (max == 0) return string.Empty;
    int len = (int)Math.Round(20.0 * value / max);
    return new string('█', len) + new string('░', 20 - len);
}

static string FormatBytes(long bytes)
{
    if (bytes >= 1L << 30) return $"{bytes / (1L << 30):F1} GB";
    if (bytes >= 1L << 20) return $"{bytes / (1L << 20):F1} MB";
    if (bytes >= 1L << 10) return $"{bytes / (1L << 10):F1} KB";
    return $"{bytes} B";
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";
