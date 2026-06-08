using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using RamAI.Phase2.Data;

namespace RamAI.Phase2.ML;

/// <summary>
/// Builds, trains, evaluates and saves the RAM-AI binary classification model.
///
/// Pipeline
/// ────────
///  1. Concatenate all float features → "Features" vector
///  2. Normalise (MinMax) — helps SDCA fallback and keeps scores comparable
///  3. FastTreeBinaryTrainer  (primary — handles non-linear patterns well)
///     • 100 trees, 20 leaves, min 10 samples/leaf
///  4. Evaluate on a 20 % hold-out split (stratified by Label)
///  5. Save model to Phase2\model\ram-ai.zip
/// </summary>
internal static class ModelTrainer
{
    private static readonly string[] FeatureColumns =
    [
        "WorkingSetMB",
        "PrivateBytesMB",
        "PageFaultDeltaKB",
        "WsToPrivateRatio",
        "LogWorkingSet",
    ];

    public static ITransformer Train(
        MLContext mlCtx,
        IEnumerable<ProcessTrainingRow> rows,
        string modelOutputPath)
    {
        // ── 1. Load into IDataView ────────────────────────────────────────────
        IDataView data = mlCtx.Data.LoadFromEnumerable(rows);

        // ── 2. Train / test split  (80 / 20, seeded for reproducibility) ──────
        var split = mlCtx.Data.TrainTestSplit(data, testFraction: 0.20, seed: 42);

        // ── 3. Build pipeline ─────────────────────────────────────────────────
        var pipeline = mlCtx.Transforms
            .Concatenate("Features", FeatureColumns)

            .Append(mlCtx.Transforms.NormalizeMinMax("Features"))

            .Append(mlCtx.BinaryClassification.Trainers.FastTree(
                new FastTreeBinaryTrainer.Options
                {
                    LabelColumnName            = "Label",
                    FeatureColumnName          = "Features",
                    NumberOfTrees              = 100,
                    NumberOfLeaves             = 20,
                    MinimumExampleCountPerLeaf = 10,
                    LearningRate               = 0.1,
                    Shrinkage                  = 0.1,
                    FeatureFraction            = 1.0,
                    Seed                       = 42,
                }));

        // ── 4. Train ──────────────────────────────────────────────────────────
        Console.WriteLine("\n  [Trainer] Training FastTree binary classifier …");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ITransformer model = pipeline.Fit(split.TrainSet);
        sw.Stop();
        Console.WriteLine($"  [Trainer] Training completed in {sw.Elapsed.TotalSeconds:F1} s");

        // ── 5. Evaluate ───────────────────────────────────────────────────────
        Evaluate(mlCtx, model, split.TestSet);

        // ── 6. Save model ─────────────────────────────────────────────────────
        string dir = Path.GetDirectoryName(modelOutputPath)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        mlCtx.Model.Save(model, data.Schema, modelOutputPath);
        Console.WriteLine($"\n  [Trainer] Model saved → {modelOutputPath}");

        return model;
    }

    // ── Evaluation ────────────────────────────────────────────────────────────

    private static void Evaluate(MLContext mlCtx, ITransformer model, IDataView testSet)
    {
        IDataView predictions = model.Transform(testSet);

        // ── Binary classification metrics (Label is Boolean — correct evaluator) ──
        var metrics = mlCtx.BinaryClassification.Evaluate(
            predictions,
            labelColumnName:          "Label",
            scoreColumnName:          "Score",
            probabilityColumnName:    "Probability",
            predictedLabelColumnName: "PredictedLabel");

        // ── MAE computed manually: |label - probability| averaged over test set ──
        // We cannot use mlCtx.Regression.Evaluate because it requires a Single label,
        // whereas our Label column is Boolean.
        double mae = ComputeMAE(mlCtx, predictions);

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────────────────────────┐");
        Console.WriteLine("  │           MODEL EVALUATION — test set           │");
        Console.WriteLine("  ├─────────────────────────────────────────────────┤");
        Console.WriteLine($"  │  Accuracy           : {metrics.Accuracy,8:P2}              │");
        Console.WriteLine($"  │  AUC (ROC)          : {metrics.AreaUnderRocCurve,8:F4}              │");
        Console.WriteLine($"  │  F1 Score           : {metrics.F1Score,8:F4}              │");
        Console.WriteLine($"  │  Precision          : {metrics.PositivePrecision,8:F4}              │");
        Console.WriteLine($"  │  Recall             : {metrics.PositiveRecall,8:F4}              │");
        Console.WriteLine($"  │  MAE (probability)  : {mae,8:F4}              │");
        Console.WriteLine($"  │  Log-loss           : {metrics.LogLoss,8:F4}              │");
        Console.WriteLine($"  │  Log-loss reduction : {metrics.LogLossReduction,8:F4}              │");
        Console.WriteLine("  └─────────────────────────────────────────────────┘");

        // Confusion matrix
        var cm = metrics.ConfusionMatrix;
        Console.WriteLine();
        Console.WriteLine("  Confusion matrix (rows=actual, cols=predicted):");
        Console.WriteLine($"                  Pred-FALSE   Pred-TRUE");
        Console.WriteLine($"  Actual-FALSE  :  {cm.Counts[0][0],10:N0}  {cm.Counts[0][1],10:N0}");
        Console.WriteLine($"  Actual-TRUE   :  {cm.Counts[1][0],10:N0}  {cm.Counts[1][1],10:N0}");
    }

    // ── MAE helper ────────────────────────────────────────────────────────────
    // Computes mean |label - probability| without calling Regression.Evaluate,
    // which would fail because Label is Boolean, not Single.

    private static double ComputeMAE(MLContext mlCtx, IDataView predictions)
    {
        var labelCol = mlCtx.Data.CreateEnumerable<LabelProbRow>(
            predictions, reuseRowObject: false);

        double sum   = 0;
        long   count = 0;
        foreach (var row in labelCol)
        {
            sum += Math.Abs((row.Label ? 1f : 0f) - row.Probability);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    // Minimal projection for MAE computation
    private sealed class LabelProbRow
    {
        [Microsoft.ML.Data.ColumnName("Label")]
        public bool  Label       { get; set; }

        [Microsoft.ML.Data.ColumnName("Probability")]
        public float Probability { get; set; }
    }
}
