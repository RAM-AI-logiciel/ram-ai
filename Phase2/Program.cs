using System.Text;
using Microsoft.ML;
using RamAI.Phase2.Data;
using RamAI.Phase2.ML;

// ── Paths ─────────────────────────────────────────────────────────────────────

// patterns.json produced by Phase 1
const string PatternsJson = @"C:\Projects\RAM-AI\Phase1\data\patterns.json";

// Where to save the trained model
const string ModelPath    = @"model\ram-ai.zip";

// ── Banner ────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine();
Console.WriteLine("  ╔══════════════════════════════════════════════════╗");
Console.WriteLine("  ║    RAM-AI Phase 2 — ML.NET Memory Predictor     ║");
Console.WriteLine("  ╚══════════════════════════════════════════════════╝");
Console.WriteLine();

// ── 1. Load & prepare data ────────────────────────────────────────────────────

Console.WriteLine("► Step 1 / 3 — Loading patterns.json …");

// Accept the path from a CLI argument to override the default
string jsonPath = args.Length > 0 ? args[0] : PatternsJson;

// Fallback: search for the file relative to the binary if the absolute path is missing
if (!File.Exists(jsonPath))
{
    string? nearby = TryFindPatterns();
    if (nearby is not null)
    {
        Console.WriteLine($"  (patterns.json found at {nearby})");
        jsonPath = nearby;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ERROR: patterns.json not found at {jsonPath}");
        Console.WriteLine("  Run Phase 1 first, or pass the path as argument:");
        Console.WriteLine("      RamAI.Phase2.exe  C:\\path\\to\\data\\patterns.json");
        Console.ResetColor();
        return 1;
    }
}

List<ProcessTrainingRow> trainingData = DataLoader.Load(jsonPath);

if (trainingData.Count < 50)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  WARNING: only {trainingData.Count} training rows — " +
                      "run Phase 1 longer for a more accurate model.");
    Console.ResetColor();
}

// ── 2. Train ──────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("► Step 2 / 3 — Training …");

var mlCtx = new MLContext(seed: 42);
ITransformer model = ModelTrainer.Train(mlCtx, trainingData, ModelPath);

// ── 3. Demo inference on current process list ─────────────────────────────────

Console.WriteLine();
Console.WriteLine("► Step 3 / 3 — Live inference on running processes …");
Console.WriteLine();

using var predictor = new ModelPredictor(mlCtx, model,
    mlCtx.Data.LoadFromEnumerable(trainingData).Schema);

// Snapshot the current process list via System.Diagnostics
var snapshots = GetCurrentSnapshots();
ProcessPrediction[] predictions = predictor.Predict(snapshots);

// Print top 20 sorted by probability
int show = Math.Min(predictions.Length, 20);
Console.WriteLine($"  {"PID",7}  {"Name",-28}  {"Prob",6}  {"Active?",8}");
Console.WriteLine($"  {new string('─', 60)}");

for (int i = 0; i < show; i++)
{
    var p = predictions[i];
    Console.ForegroundColor = p.WillAccessMemory ? ConsoleColor.Green : ConsoleColor.DarkGray;
    Console.WriteLine($"  {p.Pid,7}  {Truncate(p.Name, 28),-28}  {p.Probability,6:P1}  " +
                      $"{(p.WillAccessMemory ? "● ACTIVE" : "  idle"),8}");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine($"  Model saved → {Path.GetFullPath(ModelPath)}");
Console.WriteLine("  Done. Press any key to exit.");
Console.ReadKey(intercept: true);
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static RawSnapshot[] GetCurrentSnapshots()
{
    var list = new List<RawSnapshot>();
    foreach (var p in System.Diagnostics.Process.GetProcesses())
    {
        using (p)
        {
            try
            {
                p.Refresh();
                list.Add(new RawSnapshot
                {
                    Timestamp       = DateTime.UtcNow,
                    Pid             = p.Id,
                    Name            = p.ProcessName,
                    WorkingSetBytes = p.WorkingSet64,
                    PrivateBytes    = p.PrivateMemorySize64,
                    PageFaultDelta  = 0,   // not known for a single-shot snapshot
                });
            }
            catch { /* protected / exited */ }
        }
    }
    return [.. list];
}

static string? TryFindPatterns()
{
    // Walk up from the current exe looking for data\patterns.json
    string? dir = AppContext.BaseDirectory;
    for (int depth = 0; depth < 6 && dir is not null; depth++)
    {
        string candidate = Path.Combine(dir, "data", "patterns.json");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";
