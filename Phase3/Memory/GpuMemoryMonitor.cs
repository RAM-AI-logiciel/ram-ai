using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RamAI.Phase3.Memory;

/// <summary>
/// Monitore la VRAM partagée (Non Local Usage) via les compteurs Windows natifs.
/// "Non Local Usage" = pages GPU résidant en RAM système (pas en VRAM dédiée) —
/// c'est la fraction qui mord directement sur le budget RAM de l'OS.
///
/// Disponible sur Windows 10 1903+ avec les drivers WDDM 2.x.
/// Fallback silencieux si la catégorie est absente (VM, drivers anciens).
/// </summary>
internal sealed class GpuMemoryMonitor : IDisposable
{
    private const string CategoryName    = "GPU Process Memory";
    private const string CounterName     = "Non Local Usage";  // bytes (gauge, pas rate)
    private const int    RingSize        = 10;
    private const long   MinDeltaMb      = 50L;   // hausse > 50 Mo/tick pour signaler
    private const int    MinRisingSamples = 3;     // N ticks consécutifs en hausse

    private readonly ILogger _log;
    private readonly bool    _available;
    private bool             _loggedUnavailable;

    private List<PerformanceCounter> _counters = new();
    private readonly object          _countersLock = new();

    // Ring buffer des totaux GPU Non-Local (en Mo), alimenté par Sample()
    private readonly long[] _ring = new long[RingSize];
    private int              _ringIdx;
    private int              _sampleCount;

    /// <summary>Valeur GPU Non-Local du dernier Sample(), en Mo.</summary>
    public long TickGpuNonLocalMb { get; private set; }

    internal GpuMemoryMonitor(ILogger log)
    {
        _log       = log;
        _available = TryInitCounters();
    }

    private bool TryInitCounters()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(CategoryName))
            {
                _log.LogDebug("[GPU] Catégorie '{C}' absente — monitoring GPU Non-Local désactivé", CategoryName);
                return false;
            }
            RefreshCounters();
            return true;
        }
        catch (Exception ex)
        {
            if (!_loggedUnavailable)
            {
                _log.LogDebug("[GPU] Monitoring GPU indisponible : {M}", ex.Message);
                _loggedUnavailable = true;
            }
            return false;
        }
    }

    /// <summary>
    /// Recrée la liste des compteurs par instance.
    /// À appeler périodiquement (ex. toutes les 60 ticks) pour détecter
    /// les processus GPU qui démarrent ou s'arrêtent.
    /// </summary>
    internal void RefreshCounters()
    {
        if (!_available && _sampleCount > 0) return; // déjà marqué indispo

        List<PerformanceCounter> fresh = new();
        try
        {
            var cat       = new PerformanceCounterCategory(CategoryName);
            var instances = cat.GetInstanceNames();
            foreach (var inst in instances)
            {
                try
                {
                    var pc = new PerformanceCounter(CategoryName, CounterName, inst, readOnly: true);
                    pc.NextValue(); // premier appel toujours 0 — à jeter
                    fresh.Add(pc);
                }
                catch { /* instance disparue entre GetInstanceNames et la création */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("[GPU] RefreshCounters erreur : {M}", ex.Message);
        }

        lock (_countersLock)
        {
            var old = _counters;
            _counters = fresh;
            foreach (var c in old) try { c.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Lit la somme GPU Non-Local de toutes les instances et met à jour le ring buffer.
    /// À appeler une fois par tick depuis MemoryOrchestrator.
    /// </summary>
    internal void Sample()
    {
        if (!_available) return;

        long totalBytes = 0L;
        lock (_countersLock)
        {
            foreach (var c in _counters)
            {
                try { totalBytes += (long)c.NextValue(); }
                catch { /* instance disparue */ }
            }
        }

        TickGpuNonLocalMb           = totalBytes / (1024L * 1024L);
        _ring[_ringIdx % RingSize]  = TickGpuNonLocalMb;
        _ringIdx++;
        if (_sampleCount < RingSize) _sampleCount++;
    }

    /// <summary>
    /// Retourne true si la VRAM partagée est en hausse rapide sur les derniers ticks.
    /// Critère : N ticks consécutifs récents avec delta > MinDeltaMb chacun.
    /// </summary>
    internal bool IsGpuMemoryPressureRising()
    {
        if (!_available || _sampleCount < MinRisingSamples + 1) return false;

        int rising = 0;
        for (int i = 1; i <= MinRisingSamples; i++)
        {
            long newer = _ring[(_ringIdx - i)     % RingSize];
            long older = _ring[(_ringIdx - i - 1) % RingSize];
            if (newer - older >= MinDeltaMb)
                rising++;
            else
                break; // pas consécutif — arrêter
        }
        return rising >= MinRisingSamples;
    }

    public void Dispose()
    {
        lock (_countersLock)
        {
            foreach (var c in _counters) try { c.Dispose(); } catch { }
            _counters.Clear();
        }
    }
}
