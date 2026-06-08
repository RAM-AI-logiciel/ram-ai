using System.Text.Json.Serialization;

namespace RamAI.Phase1;

/// <summary>One measurement for a single process at a given instant.</summary>
public sealed class ProcessSnapshot
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Working Set in bytes.</summary>
    [JsonPropertyName("workingSetBytes")]
    public long WorkingSetBytes { get; init; }

    /// <summary>Private Bytes (PrivateUsage) in bytes.</summary>
    [JsonPropertyName("privateBytes")]
    public long PrivateBytes { get; init; }

    /// <summary>Delta of PagedMemorySize64 between two samples — proxy for memory access frequency.</summary>
    [JsonPropertyName("pageFaultDelta")]
    public long PageFaultDelta { get; init; }
}

/// <summary>Root document written to patterns.json.</summary>
public sealed class PatternStore
{
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    [JsonPropertyName("snapshots")]
    public List<ProcessSnapshot> Snapshots { get; set; } = [];
}
