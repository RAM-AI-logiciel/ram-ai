using Microsoft.ML;
using RamAI.Phase2.Data;

namespace RamAI.Phase2.ML;

/// <summary>
/// Wraps the saved model and exposes a strongly-typed Predict() method.
/// Can be constructed either from a live ITransformer (after training)
/// or by loading ram-ai.zip from disk (standalone inference).
/// </summary>
public sealed class ModelPredictor : IDisposable
{
    private readonly MLContext _mlCtx;
    private readonly PredictionEngine<ProcessTrainingRow, ProcessPrediction> _engine;

    // ── Construction from a live model ───────────────────────────────────────

    public ModelPredictor(MLContext mlCtx, ITransformer model, DataViewSchema inputSchema)
    {
        _mlCtx  = mlCtx;
        _engine = mlCtx.Model.CreatePredictionEngine<ProcessTrainingRow, ProcessPrediction>(
                      model, inputSchema);
    }

    // ── Construction from a saved zip ────────────────────────────────────────

    public static ModelPredictor LoadFromFile(string modelPath, MLContext? mlCtx = null)
    {
        mlCtx ??= new MLContext(seed: 42);
        var model = mlCtx.Model.Load(modelPath, out var schema);
        return new ModelPredictor(mlCtx, model, schema);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Predict memory activity for an array of raw snapshots.
    /// Returns one <see cref="ProcessPrediction"/> per input snapshot,
    /// ordered by descending probability.
    /// </summary>
    public ProcessPrediction[] Predict(RawSnapshot[] snapshots)
    {
        var results = new ProcessPrediction[snapshots.Length];

        for (int i = 0; i < snapshots.Length; i++)
        {
            var s   = snapshots[i];
            var row = ToFeatureRow(s);

            var pred = _engine.Predict(row);
            pred.Pid  = s.Pid;
            pred.Name = s.Name;

            results[i] = pred;
        }

        // Sort hottest processes first
        Array.Sort(results, (a, b) => b.Probability.CompareTo(a.Probability));
        return results;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static ProcessTrainingRow ToFeatureRow(RawSnapshot s)
    {
        float wsMB   = (float)(s.WorkingSetBytes / (1024.0 * 1024.0));
        float privMB = (float)(s.PrivateBytes    / (1024.0 * 1024.0));
        float pfKB   = (float)(Math.Min(s.PageFaultDelta, 100L * 1024 * 1024) / 1024.0);
        float ratio  = privMB > 0f ? wsMB / privMB : 1f;
        float logWS  = wsMB  > 0f ? (float)Math.Log2(wsMB) : 0f;

        return new ProcessTrainingRow
        {
            WorkingSetMB      = wsMB,
            PrivateBytesMB    = privMB,
            PageFaultDeltaKB  = pfKB,
            WsToPrivateRatio  = ratio,
            LogWorkingSet     = logWS,
            Label             = false,   // irrelevant at inference time
        };
    }

    public void Dispose() => _engine.Dispose();
}
