using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamAI.Phase3.Logging;

/// <summary>
/// Appends structured JSON-lines to logs\events.log.
/// Chaque ligne est un objet JSON autonome (format NDJSON).
/// Thread-safe via lock ; FileOptions.WriteThrough pour la durabilité.
/// </summary>
internal sealed class EventLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object       _lock = new();

    private long _totalFaultsAvoided;
    private long _totalMbSaved;
    private long _totalTicks;
    private long _totalLatencyMs;

    internal long TotalFaultsAvoided => _totalFaultsAvoided;
    internal long TotalMbSaved       => _totalMbSaved;
    internal long AverageLatencyMs   =>
        _totalTicks == 0 ? 0 : _totalLatencyMs / _totalTicks;

    internal EventLogger(string logPath)
    {
        string? dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _writer = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write,
                           FileShare.ReadWrite, 65536, FileOptions.WriteThrough),
            Encoding.UTF8)
        {
            // AutoFlush garantit que chaque WriteLine traverse le buffer .NET
            // et atteint le FileStream (WriteThrough l'écrit ensuite directement sur disque).
            // Sans AutoFlush, Phase4 LogWatcher manquait des entrées entre les Flush.
            AutoFlush = true,
        };

        AppendLine(new ServiceStartMarker { Marker = "SERVICE_START", Timestamp = DateTime.UtcNow });

        // Écrire baseline.json : RAM utilisée au démarrage du service.
        // Phase4 lit ce fichier pour ancrer le % d'amélioration sur le vrai démarrage
        // du service, pas sur l'ouverture du dashboard.
        // Écrasé à chaque SERVICE_START → toujours cohérent avec le dernier lancement.
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                long totalMb = RamAI.Phase3.Memory.NativeMemory.GetTotalPhysicalMb();
                long availMb = RamAI.Phase3.Memory.NativeMemory.GetAvailablePhysicalMb();
                long usedMb  = totalMb - availMb;
                string baselineJson = $"{{\"ramUsedMb\":{usedMb},\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
                File.WriteAllText(Path.Combine(dir, "baseline.json"), baselineJson, Encoding.UTF8);
            }
            catch { /* ne jamais bloquer le démarrage du service pour ça */ }
        }
    }

    /// <summary>Écrit une entrée de tick dans le log.</summary>
    internal void Write(EventEntry entry)
    {
        Interlocked.Add(ref _totalFaultsAvoided, entry.FaultsAvoided);
        Interlocked.Add(ref _totalMbSaved,       entry.MbSaved);
        Interlocked.Increment(ref _totalTicks);
        Interlocked.Add(ref _totalLatencyMs,     entry.LatencyMs);

        AppendLine(entry);
    }

    /// <summary>Écrit un marqueur libre (ex: "GAMING MODE ON — cs2").</summary>
    internal void WriteMarker(string text)
    {
        AppendLine(new FreeMarker { Marker = text, Timestamp = DateTime.UtcNow });
    }

    // camelCase pour correspondre aux [JsonPropertyName] de Phase4
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    private void AppendLine(object obj)
    {
        string line = JsonSerializer.Serialize(obj, JsonOpts);
        lock (_lock) { _writer.WriteLine(line); }
    }

    public void Dispose()
    {
        try
        {
            AppendLine(new ServiceStopMarker
            {
                Marker             = "SERVICE_STOP",
                Timestamp          = DateTime.UtcNow,
                TotalFaultsAvoided = _totalFaultsAvoided,
                TotalMbSaved       = _totalMbSaved,
                AverageLatencyMs   = AverageLatencyMs,
                TotalTicks         = _totalTicks,
            });
            _writer.Flush();
        }
        catch { }
        _writer.Dispose();
    }
}

// ── Marqueurs de session (types nommés — évite le renommage Obfuscar des types anonymes) ──

internal sealed class ServiceStartMarker
{
    [System.Text.Json.Serialization.JsonPropertyName("marker")]
    public string   Marker    { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }
}

internal sealed class FreeMarker
{
    [System.Text.Json.Serialization.JsonPropertyName("marker")]
    public string   Marker    { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }
}

internal sealed class ServiceStopMarker
{
    [System.Text.Json.Serialization.JsonPropertyName("marker")]
    public string   Marker             { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public DateTime Timestamp          { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("totalFaultsAvoided")]
    public long     TotalFaultsAvoided { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("totalMbSaved")]
    public long     TotalMbSaved       { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("averageLatencyMs")]
    public long     AverageLatencyMs   { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("totalTicks")]
    public long     TotalTicks         { get; init; }
}

// ── Tick entry DTO (sérialisé en JSON) ───────────────────────────────────────

internal sealed class EventEntry
{
    public DateTime Timestamp            { get; init; }
    public int      LatencyMs            { get; init; }
    public int      ColdEvicted          { get; init; }
    public int      HotPrefetched        { get; init; }
    public int      FaultsAvoided        { get; init; }
    public long     MbSaved              { get; init; }
    public long     CacheByteSaved       { get; init; }
    public bool     IsGamingMode         { get; init; }
    public string   GameName             { get; init; } = string.Empty;
    /// <summary>RAM physique effectivement libérée ce cycle (delta ullAvailPhys avant/après).</summary>
    public long     PhysicalMbFreed      { get; init; }
    /// <summary>True si le service tourne en mode éco (batterie détectée).</summary>
    public bool     IsEcoMode            { get; init; }
    public bool     IsTournamentMode     { get; init; }
    public long     VramMb               { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("swapPagesPerSec")]
    public float    SwapPagesPerSec      { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("antiSwapIntervention")]
    public bool     AntiSwapIntervention { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("intervalMs")]
    public int      IntervalMs           { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("fgPid")]
    public int      FgPid                { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("fgWsMb")]
    public long     FgWsMb               { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("fgPageFaultDeltaKB")]
    public float    FgPageFaultDeltaKB   { get; init; }
}
