using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamAI.Phase3.Logging;
using RamAI.Phase3.Memory;

namespace RamAI.Phase3.Service;

/// <summary>
/// BackgroundService wired into the Windows SCM via UseWindowsService().
/// Owns the lifecycle of MemoryOrchestrator and EventLogger.
/// </summary>
internal sealed class RamAiService : BackgroundService
{
    private readonly ILogger<RamAiService>         _log;
    private readonly IConfiguration                _cfg;
    private readonly ILogger<MemoryOrchestrator>   _orchLog;
    private readonly ILogger<PageCacheManager>     _cacheLog;

    private MemoryOrchestrator? _orchestrator;
    private EventLogger?        _events;

    public RamAiService(
        ILogger<RamAiService>       log,
        ILogger<MemoryOrchestrator> orchLog,
        ILogger<PageCacheManager>   cacheLog,
        IConfiguration              cfg)
    {
        _log      = log;
        _orchLog  = orchLog;
        _cacheLog = cacheLog;
        _cfg      = cfg;
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string baseDir = AppContext.BaseDirectory;

        // Dossier partagé Phase3 ↔ Phase4 : C:\ProgramData\RAM-AI\
        // Accessible par le service Windows (LocalSystem) ET par Phase4 (user).
        string sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI");
        Directory.CreateDirectory(sharedDir);

        string modelPath = Resolve(_cfg["RamAi:ModelPath"],
            Path.Combine(baseDir, @"..\..\Phase2\model\ram-ai.zip"));
        string cachePath = Resolve(_cfg["RamAi:CachePath"],
            Path.Combine(baseDir, @"cache\ram-ai.cache"));
        // events.log dans SharedDir : même emplacement que les fichiers flag.
        // Plus de chemin hardcodé dans appsettings.json.
        string logPath   = Resolve(_cfg["RamAi:LogPath"],
            Path.Combine(sharedDir, "events.log"));

        _log.LogInformation("RAM-AI Phase 3 starting");
        _log.LogInformation("  SharedDir : {S}", sharedDir);
        _log.LogInformation("  Model     : {M}", modelPath);
        _log.LogInformation("  Cache     : {C}", cachePath);
        _log.LogInformation("  Log       : {L}", logPath);
        Console.WriteLine($"[Phase3] events.log → {logPath}");

        // Vérification batterie au démarrage — permet de confirmer que GetSystemPowerStatus fonctionne
        bool batteryAtStart = NativeMemory.IsOnBattery();
        Console.WriteLine($"[ECO] Démarrage — vérification batterie : {batteryAtStart}");
        _log.LogInformation("[ECO] Démarrage — sur batterie : {B}", batteryAtStart);

        // Le modèle Phase2 est OPTIONNEL.
        // Sans lui, Phase3 fonctionne en mode "règles seules" :
        // gaming, turbo et éviction de base restent opérationnels.
        // La prédiction ML est désactivée et remplacée par prob = 0 (éviction systématique).
        string? resolvedModel = File.Exists(modelPath) ? modelPath : null;
        if (resolvedModel is null)
        {
            _log.LogWarning(
                "Modèle IA introuvable ({M}). Phase3 démarre en mode sans ML — " +
                "toutes les fonctionnalités (gaming, turbo, éviction) restent actives.",
                modelPath);
            Console.WriteLine($"[Phase3] AVERTISSEMENT : modèle IA absent → démarrage sans ML ({modelPath})");
        }

        _events       = new EventLogger(logPath);
        var cache     = new PageCacheManager(cachePath, _cacheLog);
        _orchestrator = new MemoryOrchestrator(_orchLog, cache, _events, resolvedModel);

        try
        {
            await _orchestrator.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* normal SCM stop */ }
        finally
        {
            _log.LogInformation(
                "RAM-AI stopped — faults avoided: {F}  MB saved: {M}  avg latency: {L}ms",
                _events.TotalFaultsAvoided, _events.TotalMbSaved, _events.AverageLatencyMs);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public override void Dispose()
    {
        _orchestrator?.Dispose();
        _events?.Dispose();
        base.Dispose();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static string Resolve(string? configured, string fallback) =>
        Path.GetFullPath(string.IsNullOrEmpty(configured) ? fallback : configured);
}
