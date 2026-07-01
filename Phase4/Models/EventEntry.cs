using System.Text.Json.Serialization;

namespace RamAI.Phase4.Models;

/// <summary>DTO correspondant à une ligne JSON de logs\events.log (Phase 3).</summary>
public sealed class EventEntry
{
    [JsonPropertyName("timestamp")]      public DateTime Timestamp      { get; init; }
    [JsonPropertyName("latencyMs")]      public int      LatencyMs      { get; init; }
    [JsonPropertyName("coldEvicted")]    public int      ColdEvicted    { get; init; }
    [JsonPropertyName("hotPrefetched")]  public int      HotPrefetched  { get; init; }
    [JsonPropertyName("faultsAvoided")]  public int      FaultsAvoided  { get; init; }
    [JsonPropertyName("mbSaved")]        public long     MbSaved        { get; init; }
    [JsonPropertyName("cacheByteSaved")] public long     CacheByteSaved { get; init; }
    [JsonPropertyName("isGamingMode")]         public bool     IsGamingMode         { get; init; }
    [JsonPropertyName("gameName")]             public string   GameName             { get; init; } = string.Empty;
    /// <summary>RAM physique effectivement libérée ce cycle (mesurée avant/après par Phase3).</summary>
    [JsonPropertyName("physicalMbFreed")]       public long     PhysicalMbFreed      { get; init; }
    [JsonPropertyName("isEcoMode")]             public bool     IsEcoMode            { get; init; }
    [JsonPropertyName("isTournamentMode")]      public bool     IsTournamentMode     { get; init; }
    [JsonPropertyName("vramMb")]                public long     VramMb               { get; init; }
    [JsonPropertyName("swapPagesPerSec")]       public float    SwapPagesPerSec      { get; init; }
    [JsonPropertyName("antiSwapIntervention")]  public bool     AntiSwapIntervention { get; init; }
    [JsonPropertyName("intervalMs")]            public int      IntervalMs           { get; init; }
    [JsonPropertyName("fgPid")]                 public int      FgPid                { get; init; }
    [JsonPropertyName("fgWsMb")]                public long     FgWsMb               { get; init; }
    [JsonPropertyName("fgPageFaultDeltaKB")]    public float    FgPageFaultDeltaKB   { get; init; }
}
