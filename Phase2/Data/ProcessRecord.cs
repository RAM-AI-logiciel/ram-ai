using Microsoft.ML.Data;

namespace RamAI.Phase2.Data;

// ─────────────────────────────────────────────────────────────────────────────
// Raw snapshot row as read from patterns.json
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RawSnapshot
{
    public DateTime Timestamp      { get; init; }
    public int      Pid            { get; init; }
    public string   Name           { get; init; } = string.Empty;
    public long     WorkingSetBytes { get; init; }
    public long     PrivateBytes   { get; init; }
    public long     PageFaultDelta { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// ML input row  (features + label)
// Produced by DataLoader: row[t] features → label derived from row[t+1]
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProcessTrainingRow
{
    // ── Features ──────────────────────────────────────────────────────────────

    /// <summary>Working Set in MB.</summary>
    [ColumnName("WorkingSetMB")]
    public float WorkingSetMB { get; init; }

    /// <summary>Private Bytes in MB.</summary>
    [ColumnName("PrivateBytesMB")]
    public float PrivateBytesMB { get; init; }

    /// <summary>Current page-fault delta (in KB, capped at 100 MB).</summary>
    [ColumnName("PageFaultDeltaKB")]
    public float PageFaultDeltaKB { get; init; }

    /// <summary>Ratio WorkingSet / PrivateBytes — memory pressure indicator.</summary>
    [ColumnName("WsToPrivateRatio")]
    public float WsToPrivateRatio { get; init; }

    /// <summary>Log2 of working set — compresses the wide range.</summary>
    [ColumnName("LogWorkingSet")]
    public float LogWorkingSet { get; init; }

    // ── Label ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// TRUE when the same process has pageFaultDelta &gt; 0
    /// in the NEXT sample (= will access memory within 2 s).
    /// </summary>
    [ColumnName("Label")]
    public bool Label { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Prediction output returned by the trained model
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProcessPrediction
{
    [ColumnName("PredictedLabel")]
    public bool WillAccessMemory { get; set; }

    /// <summary>Model confidence in the positive class [0..1].</summary>
    [ColumnName("Probability")]
    public float Probability { get; set; }

    /// <summary>Raw decision score (positive = active).</summary>
    [ColumnName("Score")]
    public float Score { get; set; }

    // ── Echoed input fields (filled by ModelPredictor, not by ML.NET) ─────────
    // [NoColumn] tells CreatePredictionEngine to ignore these properties
    // entirely — they are not part of the trained model schema.
    [NoColumn] public int    Pid  { get; set; }
    [NoColumn] public string Name { get; set; } = string.Empty;
}
