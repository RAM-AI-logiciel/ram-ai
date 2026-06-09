using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using RamAI.Phase3.Logging;

namespace RamAI.Phase3.Memory;

/// <summary>
/// Core engine — ticks every 800 ms (1200 ms en mode Gaming) :
///
///   1. Détecte si un processus de jeu est actif
///   2. Mode Gaming ON  → intervalle 500 ms, éviction agressive (seuil 0.50)
///      Mode Gaming OFF → intervalle 800 ms, seuil normal (0.20)
///   3. Cold path : K32EmptyWorkingSet + SetProcessWorkingSetSizeEx(-1,-1) + snapshot NVMe
///                  appliqué à TOUS les processus non-chauds (pas seulement froids)
///   4. Hot path  : PrefetchVirtualMemory
///   5. Mode Turbo (one-shot via turbo_mode.force) : EmptyWorkingSet sur TOUS
///      les processus y compris les services Windows non critiques
///   6. Traitement parallèle via Parallel.ForEach
/// </summary>
internal sealed class MemoryOrchestrator : IDisposable
{
    // ── Seuils et intervalles ─────────────────────────────────────────────────
    private const float ColdThreshold        = 0.20f;
    private const float ColdThresholdGaming  = 0.50f;
    private const float HotThreshold         = 0.65f;
    private const int   NormalIntervalMs     =   800;  // mode normal
    private const int   GamingIntervalMs     = 1_200;  // mode Gaming  — intervalle long pour éviter les micro-freezes cycliques
    private const int   AiIntervalMs         = 1_200;  // mode IA      — entre gaming et navigateur
    private const int   BrowserIntervalMs    = 1_500;  // mode Navigateur — le moins agressif
    private const long  AiWsThresholdMb      =   10L;  // seuil WS minimum pour évincer un processus IA

    // ── Mode Tournoi (Ultra — performances gaming absolues) ──────────────────
    private const int   TournamentIntervalMs      = 500;   // anti-stutter : 500ms (300ms causait des drops FPS)
    private const float TournamentRamThresholdPct = 0.25f; // n'agir que si RAM dispo < 25% du total
    private const float TournamentEmergencyPct    = 0.15f; // Turbo d'urgence si dispo < 15%
    private const float TournamentMaxReleasePct   = 0.10f; // libérer max 10% de la RAM récupérable par cycle
    private const long  TournamentMaxReleaseMb    =  50L;  // hard cap absolu : jamais > 50 Mo libérés par cycle
    private const float TournamentGameRamPct      = 0.80f; // suspension si jeu WS > 80% de la RAM dispo
    private const int   TournamentSuspendMs       = 2_000; // durée de suspension anti-stutter en ms

    // ── Optimisation prédictive (Ultra) ───────────────────────────────────────
    private const int  PredictiveHistorySize          = 10;
    private const int  PredictiveMinSamples           =  5;
    private const long PredictiveDropThresholdMbCycle = 50L; // 50 Mo/cycle = alerte

    // ── Intervalles mode Éco (batterie) ──────────────────────────────────────
    private const int   EcoNormalIntervalMs  = 3_000;
    private const int   EcoGamingIntervalMs  = 2_000;
    private const int   EcoAiIntervalMs      = 4_000;
    private const int   EcoBrowserIntervalMs = 4_000;
    private const int   EcoMaxProcsPerCycle  =     8;  // max processus par cycle en mode éco
    private const int   NormalMaxProcsPerCycle = 20;   // max processus par cycle en mode normal
    private const int   EcoBatchSleepMs      =   100;  // pause entre batches en mode éco
    private const int   NormalBatchSleepMs   =    50;  // pause entre batches en mode normal

    // ── Dossier partagé Phase3 ↔ Phase4 ──────────────────────────────────────
    // C:\ProgramData\RAM-AI\ est accessible par :
    //   • Phase3 tournant comme service Windows (compte LocalSystem ou service dédié)
    //   • Phase4 tournant comme application utilisateur
    //   • Les deux en dev ET après dotnet publish, quel que soit le dossier d'installation.
    private static readonly string SharedFlagDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI");

    // ── Fichiers flag partagés avec le dashboard Phase 4 ─────────────────────
    private static readonly string ForceFlagPath      = Path.Combine(SharedFlagDir, "gaming_mode.force");
    private static readonly string TurboFlagPath      = Path.Combine(SharedFlagDir, "turbo_mode.force");
    private static readonly string TournamentFlagPath = Path.Combine(SharedFlagDir, "tournament_mode.force");
    private static readonly string GamingProfilesPath = Path.Combine(SharedFlagDir, "gaming_profiles.json");

    // ── État Ultra ────────────────────────────────────────────────────────────
    private bool     _tournamentModeActive;
    private DateTime _tournamentSuspendUntil = DateTime.MinValue; // suspension 2s anti-stutter
    private long     _vramMb;   // VRAM totale de l'adaptateur (WMI, mise à jour périodique)

    // ── Optimisation prédictive — historique RAM disponible ──────────────────
    private readonly Queue<long> _availMbHistory = new();

    // ── Profils gaming par jeu ────────────────────────────────────────────────
    private Dictionary<string, GamingProfile>? _gamingProfiles;
    private DateTime                            _profilesLastLoaded = DateTime.MinValue;

    // ── Noms de processus de jeux connus ─────────────────────────────────────
    // IMPORTANT : Process.ProcessName retourne TOUJOURS le nom SANS extension .exe
    // sur Windows. "steam.exe" ne matchera jamais — utiliser "steam".
    // Les entrées avec ".exe" sont incluses pour couverture d'outils tiers qui
    // pourraient retourner le nom complet (ex : PowerShell Get-Process -Name).
    private static readonly HashSet<string> KnownGameProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // FPS / Battle Royale
            "cs2", "csgo", "valorant", "fortnite",
            "r5apex", "apex", "apex_legends",
            "cod", "modernwarfare", "mw2", "mw3",
            "battlefield", "bf2042", "bf1", "bfv",
            // RPG / Open World
            "eldenring", "cyberpunk2077", "witcher3", "fallout4",
            "skyrim", "skyrimse", "hogwartslegacy",
            // Sandbox / Créatif
            "minecraft", "javaw", "roblox", "robloxplayerbeta",
            // GTA
            "gta5", "gtav", "gta_sa",
            // MOBA / Hero
            "dota2", "leagueoflegends", "lol", "overwatch", "overwatch2",
            // MMO / Divers
            "destiny2", "warframe", "rainbowsix", "r6",
            // Lanceurs et overlays — sans .exe (format Process.ProcessName)
            "steam",            "steam.exe",        // steam.exe → ProcessName="steam"
            "steamwebhelper",   "steamwebhelper.exe",
            "gameoverlayui",    "gameoverlayui.exe",
            "epicgameslauncher","epicgameslauncher.exe",
            "fortnitelauncher",
            "origin",           "origin.exe",
            "eadesktop",        "eaapp",
            "uplay",            "ubisoftconnect",
            "battlenet",        "gogalaxy",
            "riotclientservices","riotclient",
            // ── TEST UNIQUEMENT — à retirer avant release commerciale ──────────
            "notepad",   // Bloc-notes Windows : ouvrir pour simuler un jeu détecté
        };

    // ── Applications IA locales et services IA cloud ─────────────────────────
    // SÉPARÉ de KnownGameProcesses et KnownBrowserProcesses.
    // Un outil IA ne doit PAS déclencher le Mode Gaming ni le Mode Navigateur.
    private static readonly HashSet<string> KnownAiProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Runtimes IA locaux
            "ollama",           "ollama.exe",
            "lmstudio",         "lmstudio.exe",
            "jan",              "jan.exe",
            "koboldcpp",        "koboldcpp.exe",
            "textgenwebui",
            // Clients desktop IA
            "claude",           "claude.exe",       // Claude desktop
            "chatgpt",          "chatgpt.exe",      // ChatGPT desktop
            "perplexity",       "perplexity.exe",
            "gemini",           "gemini.exe",
            // Processus worker courants (inférence GPU/CPU)
            "llama-server",     "llama_server",
            "python",           "python3",          // scripts IA (large, intentionnel)
        };

    // ── Navigateurs connus — SÉPARÉ de KnownGameProcesses ────────────────────
    // Séparation intentionnelle : un navigateur ne doit PAS déclencher le Mode Gaming.
    // Process.ProcessName retourne le nom sans .exe. Les noms .exe sont inclus
    // pour compatibilité avec d'éventuels outils tiers.
    private static readonly HashSet<string> KnownBrowserProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",   "chrome.exe",
            "msedge",   "msedge.exe",
            "firefox",  "firefox.exe",
            "brave",    "brave.exe",
            "opera",    "opera.exe",
            "vivaldi",  "vivaldi.exe",
            "arc",      "arc.exe",
            // Processus de rendu / GPU communs (même nom que le navigateur principal)
            // Chrome, Edge, Brave : tous leurs enfants s'appellent pareil
        };

    // ── Processus système exclus en mode normal (inclus en mode Turbo) ────────
    private static readonly HashSet<string> SystemProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "registry", "smss", "csrss", "wininit", "winlogon",
            "lsass", "services", "svchost", "dwm",
            "memory compression",
            "msmpeng", "msmpsvc", "securityhealthservice",
            "explorer", "taskmgr", "shellexperiencehost",
            "startmenuexperiencehost", "searchhost", "searchindexer",
            "runtimebroker", "applicationframehost",
            "spoolsv", "audiodg", "fontdrvhost", "sihost",
        };

    // ── Processus protégés supplémentaires en mode Gaming ────────────────────
    // dwm et audiodg sont déjà dans SystemProcesses (jamais touchés en mode normal).
    // On ajoute ici les processus GPU NVIDIA spécifiques. Les préfixes AMD* et igfx*
    // sont gérés par une vérification StartsWith dans le code gaming.
    private static readonly HashSet<string> GamingProtectedProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "nvcontainer", "nvdisplay.container",
        };

    // ── Processus JAMAIS évincés en mode Tournoi ──────────────────────────────
    // Anti-cheat : leur WS ne doit jamais être touché sous peine de ban/kick.
    // Overlays   : Discord, Steam, GFE doivent rester réactifs pendant le jeu.
    private static readonly HashSet<string> TournamentProtectedProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Anti-cheat
            "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_launcher",
            "BEService", "BEDaisy", "BattlEye",
            // Valve / Steam
            "steam", "steamwebhelper", "gameoverlayui",
            // Discord (toutes variantes)
            "Discord", "DiscordPTB", "DiscordCanary",
            // NVIDIA GeForce Experience / overlay
            "NVIDIA GeForce Experience", "nvcontainer", "nvdisplay.container",
            "NvNode", "NvNodeLauncher", "NvTelemetryContainer",
            "GfeClientsService",
        };

    // ── État gaming ───────────────────────────────────────────────────────────
    private bool   _gamingModeActive;
    private string _currentGame = string.Empty;

    // ── État IA ───────────────────────────────────────────────────────────────
    private bool   _aiModeActive;
    private string _currentAiApp = string.Empty;

    // ── État navigateur ───────────────────────────────────────────────────────
    private bool   _browserModeActive;
    private string _currentBrowser   = string.Empty;
    private int    _browserTabsThisTick;

    // ── Suivi CPU pour les processus IA (delta entre ticks) ───────────────────
    private readonly ConcurrentDictionary<int, TimeSpan> _prevCpuTime = new();

    // ── Mode Éco (batterie) ───────────────────────────────────────────────────
    private bool _ecoMode;

    /// <summary>Nombre max de processus à traiter par cycle selon le mode éco.</summary>
    private int MaxProcsPerCycle => _ecoMode ? EcoMaxProcsPerCycle : NormalMaxProcsPerCycle;

    /// <summary>Pause en ms entre chaque batch de processus.</summary>
    private int BatchSleepMs => _ecoMode ? EcoBatchSleepMs : NormalBatchSleepMs;

    // ── Intervalle courant (recalculé par UpdateInterval()) ───────────────────
    private int _intervalMs = NormalIntervalMs;

    /// <summary>
    /// Recalcule l'intervalle selon la priorité et le mode éco :
    /// Gaming > IA > Navigateur > Normal   ×2-5 si sur batterie.
    /// </summary>
    private void UpdateInterval()
    {
        int gaming  = _ecoMode ? EcoGamingIntervalMs  : GamingIntervalMs;
        int ai      = _ecoMode ? EcoAiIntervalMs      : AiIntervalMs;
        int browser = _ecoMode ? EcoBrowserIntervalMs : BrowserIntervalMs;
        int normal  = _ecoMode ? EcoNormalIntervalMs  : NormalIntervalMs;

        _intervalMs = _gamingModeActive  ? gaming  :
                      _aiModeActive      ? ai      :
                      _browserModeActive ? browser :
                                           normal;
    }

    // ── Dépendances ───────────────────────────────────────────────────────────
    private readonly ILogger<MemoryOrchestrator>                _log;
    private readonly PageCacheManager                           _cache;
    private readonly EventLogger                                _events;
    private readonly MLContext?                                 _mlCtx;
    // Nullable : null si le modèle Phase2 est absent (mode sans ML)
    private readonly PredictionEngine<MlInputRow, MlOutputRow>? _engine;
    private readonly object                                     _predictLock = new();
    // ConcurrentDictionary pour accès thread-safe depuis Parallel.ForEach
    private readonly ConcurrentDictionary<int, uint>            _prevFaults  = new();

    // Indique si la prédiction ML est disponible
    internal bool IsMlEnabled => _engine is not null;

    /// <param name="modelPath">
    /// Chemin vers ram-ai.zip, ou <c>null</c> si Phase2 n'a pas encore été exécuté.
    /// Quand null, Phase3 fonctionne sans ML (éviction systématique, prob = 0).
    /// </param>
    internal MemoryOrchestrator(
        ILogger<MemoryOrchestrator> log,
        PageCacheManager cache,
        EventLogger events,
        string? modelPath)
    {
        _log    = log;
        _cache  = cache;
        _events = events;

        if (modelPath is not null)
        {
            _log.LogInformation("Loading ML model: {P}", modelPath);
            _mlCtx  = new MLContext(seed: 42);
            var model = _mlCtx.Model.Load(modelPath, out _);
            _engine   = _mlCtx.Model.CreatePredictionEngine<MlInputRow, MlOutputRow>(model);
            _log.LogInformation("Model loaded — prédiction ML active.");
        }
        else
        {
            _log.LogWarning("Mode sans ML : prédiction désactivée. " +
                            "Gaming, Turbo et éviction restent opérationnels.");
            Console.WriteLine("[Phase3] Mode sans ML : tous les processus seront évincés (prob=0).");
        }
    }

    // ── Boucle principale ─────────────────────────────────────────────────────

    internal async Task RunAsync(CancellationToken ct)
    {
        // Créer le dossier partagé si nécessaire (écrit et lu par Phase4 aussi)
        Directory.CreateDirectory(SharedFlagDir);
        _log.LogInformation("SharedFlagDir : {D}", SharedFlagDir);
        _log.LogInformation("ForceFlagPath : {F}", ForceFlagPath);
        _log.LogInformation("TurboFlagPath : {T}", TurboFlagPath);

        _cache.Open();
        _log.LogInformation(
            "MemoryOrchestrator running (cold<{C:P0} hot>{H:P0}, intervalle={I}ms)",
            ColdThreshold, HotThreshold, NormalIntervalMs);

        _log.LogInformation("MemoryOrchestrator prêt — détection gaming active (intervalle={I}ms)", NormalIntervalMs);

        // Le thread du service tourne toujours en BelowNormal pour réduire
        // la pression sur le scheduler Windows et limiter la consommation CPU.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

        while (!ct.IsCancellationRequested)
        {
            // Vérifier l'état batterie toutes les 20 ticks
            if (_tickCount % 20 == 0)
                RefreshEcoMode();

            // Mettre à jour la VRAM toutes les 60 ticks (~1 min)
            if (_tickCount % 60 == 0)
                RefreshVramInfo();

            var sw = Stopwatch.StartNew();
            try   { Tick(); }
            catch (Exception ex) { _log.LogError(ex, "Tick error"); }
            sw.Stop();

            int delay = _intervalMs - (int)sw.ElapsedMilliseconds;
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    // Compteur de ticks pour les logs périodiques (évite de flooder)
    private int _tickCount;

    private void Tick()
    {
        var sw = Stopwatch.StartNew();
        int  coldEvicted        = 0;
        int  hotPrefetch        = 0;
        int  faultsAvoided      = 0;
        long mbSaved            = 0;
        int  browserTabsEvicted = 0;
        int  aiProcsEvicted     = 0;

        Process[] allProcs = Process.GetProcesses();

        // ── 1. Détection gaming ───────────────────────────────────────────────
        var (isGaming, gameName) = DetectGaming(allProcs);
        ApplyGamingMode(isGaming, gameName);

        if (_gamingModeActive)
        {
            // Gaming prioritaire : désactiver IA et navigateur si actifs
            if (_aiModeActive)      ApplyAiMode(false, string.Empty);
            if (_browserModeActive) ApplyBrowserMode(false, string.Empty);
        }
        else
        {
            // ── 2. Détection IA (priorité sur navigateur) ─────────────────────
            var (isAi, aiName) = DetectAi(allProcs);
            ApplyAiMode(isAi, aiName);

            // ── 3. Détection navigateur (seulement si pas en mode IA) ─────────
            if (!_aiModeActive)
            {
                var (isBrowser, browserName) = DetectBrowser(allProcs);
                ApplyBrowserMode(isBrowser, browserName);
            }
            else if (_browserModeActive)
            {
                // Mode IA prend la priorité sur le navigateur
                ApplyBrowserMode(false, string.Empty);
            }
        }

        float coldThreshold = _gamingModeActive ? ColdThresholdGaming : ColdThreshold;

        // ── 3. Mode Tournoi (Ultra) — appliqué en priorité si gaming actif ────
        bool tournamentMode = _gamingModeActive && File.Exists(TournamentFlagPath);
        if (tournamentMode != _tournamentModeActive)
        {
            _tournamentModeActive = tournamentMode;
            if (_tournamentModeActive)
            {
                _intervalMs = TournamentIntervalMs;
                // Abaisser la priorité du service RAM-AI pour que le jeu obtienne
                // un maximum de temps CPU (le jeu est déjà élevé à High ci-dessus).
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                _log.LogInformation("[TOURNOI] Mode Tournoi ON — intervalle {I}ms, priorité BelowNormal, seuil {T:P0}",
                    TournamentIntervalMs, TournamentRamThresholdPct);
                Console.WriteLine($"[RAM-AI] 🏆 MODE TOURNOI ON (intervalle={TournamentIntervalMs}ms, seuil RAM={TournamentRamThresholdPct:P0})");
                _events.WriteMarker("TOURNAMENT MODE ON");
            }
            else
            {
                UpdateInterval();
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                _log.LogInformation("[TOURNOI] Mode Tournoi OFF — retour intervalle {I}ms", _intervalMs);
                Console.WriteLine($"[RAM-AI] 🏆 MODE TOURNOI OFF (intervalle={_intervalMs}ms)");
                _events.WriteMarker("TOURNAMENT MODE OFF");
            }
        }

        // ── 3b. Détection mode Turbo (one-shot) ──────────────────────────────
        bool turboMode = File.Exists(TurboFlagPath);

        // ── 3c. Optimisation prédictive (Ultra) ──────────────────────────────
        if (!_gamingModeActive && !_tournamentModeActive)
            CheckPredictiveOptimization(allProcs, ref coldEvicted, ref mbSaved);

        // ── 4. Éviction / Prefetch ────────────────────────────────────────────
        if (_gamingModeActive)
        {
            // ── Mode Gaming : traitement léger pour ne pas impacter les FPS ──
            // Thread déjà en BelowNormal (défini une fois pour toutes dans RunAsync)
            var swGaming = Stopwatch.StartNew();
            int gamingProcessed = 0;

            // Trier les processus : jeu à part, tout le reste filtré
            var gameProcs  = new List<Process>();
            var otherProcs = new List<Process>();

            foreach (var p in allProcs)
            {
                try
                {
                    string n = p.ProcessName;
                    if (IsGameProcess(n))
                    {
                        gameProcs.Add(p);
                    }
                    else if (!SystemProcesses.Contains(n)
                          && !GamingProtectedProcesses.Contains(n)
                          && !n.StartsWith("amd",  StringComparison.OrdinalIgnoreCase)
                          && !n.StartsWith("igfx", StringComparison.OrdinalIgnoreCase))
                    {
                        otherProcs.Add(p);
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
                catch { p.Dispose(); }
            }

            // 1. Jeu : PriorityClass = High + PrefetchHot (priorité absolue CPU + RAM)
            foreach (var gameProc in gameProcs)
            {
                using (gameProc)
                {
                    try
                    {
                        gameProc.Refresh();
                        try { gameProc.PriorityClass = ProcessPriorityClass.High; } catch { }
                        int f = PrefetchHot(gameProc);
                        Interlocked.Add(ref faultsAvoided, f);
                        Interlocked.Increment(ref hotPrefetch);
                    }
                    catch { }
                }
            }

            // 2. Traiter les autres processus
            //    Mode Tournoi : illimité + profil par jeu, mais avec seuil RAM et cap
            //    Mode normal  : limité à MaxProcsPerCycle + profil par jeu
            LoadGamingProfilesIfNeeded();
            var profile  = GetGameProfile(_currentGame);
            int maxProcs = _tournamentModeActive ? int.MaxValue
                         : (profile?.MaxProcs ?? MaxProcsPerCycle);
            int sleepMs  = _tournamentModeActive ? 0 : BatchSleepMs;

            if (profile is not null && !_tournamentModeActive)
                _log.LogInformation("[Ultra] Profil jeu '{G}' : maxProcs={M}", _currentGame, maxProcs);

            // ── Vérification seuil RAM pour le mode Tournoi ──────────────────
            bool skipTournamentEviction = false;
            long tournamentCapMb        = long.MaxValue; // cap de libération par cycle

            if (_tournamentModeActive)
            {
                long totalMb   = NativeMemory.GetTotalPhysicalMb();
                long availMb   = NativeMemory.GetAvailablePhysicalMb();
                float availPct = totalMb > 0 ? (float)availMb / totalMb : 0f;

                // ── Suspension anti-stutter 2s : jeu WS > 80% RAM dispo ─────────
                if (DateTime.UtcNow < _tournamentSuspendUntil)
                {
                    _log.LogDebug("[TOURNOI] Cycle suspendu (anti-stutter 2s actif)");
                    Console.WriteLine($"[RAM-AI] 🏆 Tournoi: cycle suspendu (anti-stutter 2s)");
                    foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                    otherProcs.Clear();
                    skipTournamentEviction = true;
                }
                else
                {
                    long gameWsMb = GetGameWorkingSetMb(_currentGame);
                    if (availMb > 0L && gameWsMb > (long)(availMb * TournamentGameRamPct))
                    {
                        _tournamentSuspendUntil = DateTime.UtcNow.AddMilliseconds(TournamentSuspendMs);
                        _log.LogInformation(
                            "[TOURNOI] Jeu WS={W}Mo > 80% RAM dispo ({A}Mo) → suspension {S}ms",
                            gameWsMb, availMb, TournamentSuspendMs);
                        Console.WriteLine(
                            $"[RAM-AI] 🏆 Tournoi: jeu {gameWsMb}Mo > 80% RAM dispo ({availMb}Mo) → suspension 2s");
                        foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                        otherProcs.Clear();
                        skipTournamentEviction = true;
                    }
                }

                if (!skipTournamentEviction && availPct < TournamentEmergencyPct)
                {
                    // Urgence < 15% : Turbo immédiat sur tous les processus non-protégés
                    _log.LogWarning("[TOURNOI] 🚨 URGENCE RAM {P:P0} < {E:P0} — Turbo d'urgence", availPct, TournamentEmergencyPct);
                    Console.WriteLine($"[RAM-AI] 🚨 TOURNOI URGENCE : RAM {availPct:P0} < 15% → Turbo d'urgence !");
                    _events.WriteMarker($"TOURNAMENT EMERGENCY TURBO — avail={availMb}Mo ({availPct:P0})");
                    foreach (var ep in otherProcs)
                    {
                        try
                        {
                            if (!TournamentProtectedProcesses.Contains(ep.ProcessName))
                            {
                                long freed = EvictTurbo(ep);
                                if (freed >= 0) { Interlocked.Increment(ref coldEvicted); Interlocked.Add(ref mbSaved, freed); gamingProcessed++; }
                            }
                        }
                        catch { }
                        finally { try { ep.Dispose(); } catch { } }
                    }
                    otherProcs.Clear();
                    skipTournamentEviction = true;
                }
                else if (availPct >= TournamentRamThresholdPct)
                {
                    // RAM suffisante (≥ 25%) : pas besoin d'agir → zéro stutter
                    _log.LogDebug("[TOURNOI] RAM dispo {P:P0} ≥ {T:P0} — cycle sauté", availPct, TournamentRamThresholdPct);
                    Console.WriteLine($"[RAM-AI] 🏆 Tournoi: RAM {availPct:P0} OK — cycle sauté (anti-stutter)");
                    foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                    otherProcs.Clear();
                    skipTournamentEviction = true;
                }
                else
                {
                    // Entre 15% et 25% : max 10% récupérable ET hard cap 50 Mo
                    long reclaimableMb = (long)(totalMb * TournamentRamThresholdPct) - availMb;
                    tournamentCapMb    = Math.Min(
                        Math.Max(0L, (long)(reclaimableMb * TournamentMaxReleasePct)),
                        TournamentMaxReleaseMb);
                    _log.LogInformation("[TOURNOI] RAM {P:P0} — cap libération = {C}Mo ce cycle (max {M}Mo)", availPct, tournamentCapMb, TournamentMaxReleaseMb);
                    Console.WriteLine($"[RAM-AI] 🏆 Tournoi: RAM {availPct:P0}, libération plafonnée à {tournamentCapMb}Mo (max {TournamentMaxReleaseMb}Mo)");
                }
            }

            var limited = skipTournamentEviction
                ? new List<Process>()
                : (maxProcs == int.MaxValue ? otherProcs : otherProcs.Take(maxProcs)).ToList();
            if (!skipTournamentEviction && maxProcs != int.MaxValue)
                foreach (var p in otherProcs.Skip(maxProcs)) try { p.Dispose(); } catch { }

            const int batchSize = 5;
            long tournamentMbThisCycle = 0L;

            for (int i = 0; i < limited.Count; i += batchSize)
            {
                // Mode Tournoi : arrêter si le cap de libération est atteint
                if (_tournamentModeActive && tournamentMbThisCycle >= tournamentCapMb) break;

                int end = Math.Min(i + batchSize, limited.Count);
                for (int j = i; j < end; j++)
                {
                    using var proc = limited[j];
                    try
                    {
                        // Mode Tournoi : ne jamais toucher les processus protégés
                        if (_tournamentModeActive && TournamentProtectedProcesses.Contains(proc.ProcessName))
                            continue;

                        proc.Refresh();
                        float prob = Predict(proc);
                        long freed = EvictProcess(proc, prob, storeToColdCache: prob < coldThreshold);
                        if (freed >= 0)
                        {
                            Interlocked.Increment(ref coldEvicted);
                            Interlocked.Add(ref mbSaved, freed);
                            if (_tournamentModeActive)
                            {
                                tournamentMbThisCycle += freed;
                                Thread.Sleep(100); // anti-stutter : délai 100ms après libération
                            }
                        }
                        gamingProcessed++;
                    }
                    catch { }
                }
                if (end < limited.Count)
                    Thread.Sleep(sleepMs);
            }

            swGaming.Stop();

            _log.LogInformation("[Gaming] Cycle optimisation — jeu exclu, {N} processus traités en {T}ms",
                gamingProcessed, swGaming.ElapsedMilliseconds);
        }
        else if (_aiModeActive)
        {
            // ── Mode IA : traitement léger pour ne pas interrompre l'app IA ──
            // Thread déjà en BelowNormal (défini une fois pour toutes dans RunAsync)
            var swAi = Stopwatch.StartNew();
            int aiProcessed = 0;

            // Trier : processus IA d'un côté, reste (filtré) de l'autre
            var aiProcs    = new List<Process>();
            var otherProcs = new List<Process>();

            foreach (var p in allProcs)
            {
                try
                {
                    string n = p.ProcessName;
                    if (IsAiProcess(n))
                        aiProcs.Add(p);
                    else if (!SystemProcesses.Contains(n)
                          && !GamingProtectedProcesses.Contains(n)
                          && !n.StartsWith("amd",  StringComparison.OrdinalIgnoreCase)
                          && !n.StartsWith("igfx", StringComparison.OrdinalIgnoreCase))
                        otherProcs.Add(p);
                    else
                        p.Dispose();
                }
                catch { p.Dispose(); }
            }

            // 1. Traiter les processus IA (éviction si inactif et WS > seuil)
            _log.LogInformation("[IA] Processus IA trouvés : {F} (app principale='{A}', seuil={T}Mo)",
                aiProcs.Count, _currentAiApp, AiWsThresholdMb);

            int aiEligible = 0;
            foreach (var aiProc in aiProcs)
            {
                using (aiProc)
                {
                    try
                    {
                        long wsMb = ReadWorkingSetMb(aiProc.Id);
                        bool aboveThreshold = wsMb >= AiWsThresholdMb;
                        if (aboveThreshold) aiEligible++;

                        _log.LogInformation("[IA] Processus {N} (PID {P}) : WS={W}Mo, éligible={E}",
                            aiProc.ProcessName, aiProc.Id, wsMb, aboveThreshold);

                        if (aboveThreshold && IsAiProcessInactive(aiProc))
                        {
                            long freed = EvictTurbo(aiProc);
                            if (freed >= 0)
                            {
                                Interlocked.Increment(ref aiProcsEvicted);
                                Interlocked.Add(ref mbSaved, freed);
                            }
                        }
                        else
                        {
                            UpdateCpuTracking(aiProc);
                        }
                        aiProcessed++;
                    }
                    catch { }
                }
            }

            _log.LogInformation("[IA] Bilan : {F} trouvés, {E} éligibles (WS>={T}Mo), {V} évincés",
                aiProcs.Count, aiEligible, AiWsThresholdMb, aiProcsEvicted);

            // 2. Traiter max MaxProcsPerCycle autres processus, par batch de 5 avec pause entre
            int aiMaxProcs = MaxProcsPerCycle;
            int aiSleepMs  = BatchSleepMs;
            var limited = otherProcs.Take(aiMaxProcs).ToList();
            foreach (var p in otherProcs.Skip(aiMaxProcs)) try { p.Dispose(); } catch { }

            const int aiBatchSize = 5;
            for (int i = 0; i < limited.Count; i += aiBatchSize)
            {
                int end = Math.Min(i + aiBatchSize, limited.Count);
                for (int j = i; j < end; j++)
                {
                    using var proc = limited[j];
                    try
                    {
                        proc.Refresh();
                        float prob = Predict(proc);
                        long freed = EvictProcess(proc, prob, storeToColdCache: prob < coldThreshold);
                        if (freed >= 0)
                        {
                            Interlocked.Increment(ref coldEvicted);
                            Interlocked.Add(ref mbSaved, freed);
                        }
                        aiProcessed++;
                    }
                    catch { }
                }
                if (end < limited.Count) Thread.Sleep(aiSleepMs);
            }

            swAi.Stop();

            _log.LogInformation("[IA] Cycle optimisation — {N} processus traités en {T}ms",
                aiProcessed, swAi.ElapsedMilliseconds);
        }
        else
        {
            // ── Modes Normal / Navigateur / Turbo : traitement parallèle complet ──
            Parallel.ForEach(
                allProcs,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                proc =>
            {
                using (proc)
                {
                    try
                    {
                        proc.Refresh();
                        string procName = proc.ProcessName;
                        long   wsBefore = proc.WorkingSet64;

                        if (turboMode)
                        {
                            // ── Mode Turbo : tous les processus sans exception ──
                            long freed = EvictTurbo(proc);
                            if (freed >= 0)
                            {
                                Interlocked.Increment(ref coldEvicted);
                                Interlocked.Add(ref mbSaved, freed);
                            }
                            return;
                        }

                        // ── Mode Navigateur : éviction ciblée des onglets ─────
                        if (_browserModeActive && IsBrowserProcess(procName))
                        {
                            if (wsBefore > 50L * 1024 * 1024)
                            {
                                long freed = EvictTurbo(proc);
                                if (freed >= 0)
                                {
                                    Interlocked.Increment(ref browserTabsEvicted);
                                    Interlocked.Add(ref mbSaved, freed);
                                }
                            }
                            return;
                        }

                        // Mode normal : exclure les processus système
                        if (SystemProcesses.Contains(procName)) return;

                        float prob = Predict(proc);

                        if (prob > HotThreshold)
                        {
                            int f = PrefetchHot(proc);
                            Interlocked.Add(ref faultsAvoided, f);
                            Interlocked.Increment(ref hotPrefetch);
                        }
                        else
                        {
                            long freed = EvictProcess(proc, prob, storeToColdCache: prob < coldThreshold);
                            if (freed >= 0)
                            {
                                Interlocked.Increment(ref coldEvicted);
                                Interlocked.Add(ref mbSaved, freed);
                            }
                        }
                    }
                    catch { /* processus terminé ou accès refusé */ }
                }
            });
        }

        _browserTabsThisTick = browserTabsEvicted;
        _ = aiProcsEvicted; // utilisé dans EventEntry ci-dessous

        // Supprimer le flag Turbo après la passe (one-shot)
        if (turboMode)
        {
            try { File.Delete(TurboFlagPath); } catch { }
            _events.WriteMarker("TURBO PASS COMPLETED");
            _log.LogInformation("Turbo pass completed: cold={C} saved={M}MB", coldEvicted, mbSaved);
        }

        sw.Stop();

        _events.Write(new EventEntry
        {
            Timestamp              = DateTime.UtcNow,
            LatencyMs              = (int)sw.ElapsedMilliseconds,
            ColdEvicted            = coldEvicted,
            HotPrefetched          = hotPrefetch,
            FaultsAvoided          = faultsAvoided,
            MbSaved                = mbSaved,
            CacheByteSaved         = _cache.TotalBytesSaved,
            IsGamingMode           = _gamingModeActive,
            GameName               = _currentGame,
            IsBrowserMode          = _browserModeActive,
            BrowserName            = _currentBrowser,
            BrowserTabsOptimized   = browserTabsEvicted,
            IsAiMode               = _aiModeActive,
            AiName                 = _currentAiApp,
            AiProcessesOptimized   = aiProcsEvicted,
            PhysicalMbFreed        = mbSaved,
            IsEcoMode              = _ecoMode,
            IsTournamentMode       = _tournamentModeActive,
            VramMb                 = _vramMb,
        });

        _log.LogDebug(
            "Tick {L}ms | cold={C} hot={H} saved≈{M}MB gaming={G} ai={A}({AP}procs) browser={B}({T}tabs) turbo={U}",
            sw.ElapsedMilliseconds, coldEvicted, hotPrefetch, mbSaved,
            _gamingModeActive, _aiModeActive, aiProcsEvicted,
            _browserModeActive, browserTabsEvicted, turboMode);
    }

    // ── Détection gaming ──────────────────────────────────────────────────────

    // Rendu non-static pour accéder à _log et _tickCount pour les diagnostics.
    private (bool IsGaming, string GameName) DetectGaming(Process[] procs)
    {
        bool logThisTick = (++_tickCount % 10 == 1); // logguer 1 tick sur 10 (~8s)

        string flagContent = ReadFlagContent();

        // Mode forcé manuellement depuis le dashboard
        if (flagContent == "manual")
        {
            if (logThisTick)
            {
                _log.LogInformation("Scan gaming: flag=manual, mode forcé actif");
                Console.WriteLine("[RAM-AI] Scan gaming: flag=manual → MODE GAMING FORCÉ");
            }
            return (true, "Mode forcé (dashboard)");
        }

        // Scan des processus en cours
        if (logThisTick)
        {
            _log.LogInformation("Scan gaming: {N} processus en cours, recherche dans liste...", procs.Length);
            Console.WriteLine($"[RAM-AI] Scan gaming: {procs.Length} processus trouvés, recherche dans liste...");
        }

        foreach (var p in procs)
        {
            try
            {
                string name = p.ProcessName;
                if (KnownGameProcesses.Contains(name))
                {
                    _log.LogInformation("MATCH GAMING : {G} (pid={P})", name, p.Id);
                    Console.WriteLine($"[RAM-AI] MATCH GAMING : {name}  (pid={p.Id})");
                    return (true, name);
                }
            }
            catch { /* processus disparu ou accès refusé */ }
        }

        // Aucun jeu connu par son nom.
        //
        // Fallback >1 Go : seulement pour DÉCLENCHER le mode gaming (pas pour le maintenir).
        // CRITIQUE : si _gamingModeActive est déjà vrai (ex : notepad vient d'être fermé),
        // ce bloc est ignoré pour permettre la désactivation du mode gaming.
        // Sans cette condition, un processus lourd quelconque (Chrome, VS Code…) empêcherait
        // la désactivation car il maintiendrait la détection "auto" en vie indéfiniment.
        // En mode Tournoi, on ne veut pas que la détection auto >1Go
        // interfère — le mode gaming est déjà actif et contrôlé manuellement.
        if (!_gamingModeActive && !_tournamentModeActive && flagContent == "auto")
        {
            foreach (var p in procs)
            {
                try
                {
                    string name = p.ProcessName;
                    if (p.WorkingSet64 > 1L * 1024 * 1024 * 1024
                        && !SystemProcesses.Contains(name)
                        && !name.Equals("RamAI.Phase3", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("RamAI.Phase4", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogInformation("MATCH GAMING (>1Go RAM) : {G} (pid={P})", name, p.Id);
                        Console.WriteLine($"[RAM-AI] MATCH GAMING (>1Go RAM) : {name} (pid={p.Id})");
                        return (true, $"{name} (>1Go RAM)");
                    }
                }
                catch { }
            }
        }

        // Aucun jeu détecté → si le mode était actif, ApplyGamingMode() va le désactiver
        if (_gamingModeActive)
        {
            _log.LogInformation("Aucun jeu détecté - désactivation gaming");
            Console.WriteLine("[RAM-AI] Aucun jeu détecté - désactivation gaming");
        }
        else if (logThisTick)
        {
            Console.WriteLine("[RAM-AI] Scan gaming: aucun jeu détecté.");
        }

        return (false, string.Empty);
    }

    private static string ReadFlagContent()
    {
        try
        {
            return File.Exists(ForceFlagPath)
                ? File.ReadAllText(ForceFlagPath).Trim()
                : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool IsGameProcess(string name)    => KnownGameProcesses.Contains(name);
    private static bool IsBrowserProcess(string name) => KnownBrowserProcesses.Contains(name);

    // ── Détection IA ─────────────────────────────────────────────────────────

    private static (bool IsAi, string AiName) DetectAi(Process[] procs)
    {
        foreach (var p in procs)
        {
            try
            {
                if (KnownAiProcesses.Contains(p.ProcessName))
                    return (true, p.ProcessName);
            }
            catch { }
        }
        return (false, string.Empty);
    }

    private void ApplyAiMode(bool isAi, string aiName)
    {
        if (isAi && !_aiModeActive)
        {
            _aiModeActive  = true;
            _currentAiApp  = aiName;
            UpdateInterval();
            _events.WriteMarker($"AI MODE ON — {aiName}");
            _log.LogInformation(">>> AI mode ON  : {A} (intervalle {I}ms)", aiName, AiIntervalMs);
            Console.WriteLine($"[RAM-AI] >>> AI MODE ON  : {aiName} (intervalle={AiIntervalMs}ms)");
        }
        else if (!isAi && _aiModeActive)
        {
            _aiModeActive = false;
            _currentAiApp = string.Empty;
            UpdateInterval();
            _events.WriteMarker("AI MODE OFF");
            _log.LogInformation(">>> AI mode OFF : intervalle {I}ms", _intervalMs);
            Console.WriteLine($"[RAM-AI] >>> AI MODE OFF (intervalle={_intervalMs}ms)");
        }
    }

    // ── Ultra : Optimisation prédictive ──────────────────────────────────────

    /// <summary>
    /// Maintient un historique des Mo de RAM disponibles.
    /// Si la RAM disponible chute de plus de 50 Mo/cycle sur les 5 derniers cycles,
    /// déclenche une éviction préventive des processus non-hot.
    /// </summary>
    private void CheckPredictiveOptimization(Process[] allProcs,
        ref int coldEvicted, ref long mbSaved)
    {
        long availMb = NativeMemory.GetAvailablePhysicalMb();
        _availMbHistory.Enqueue(availMb);
        if (_availMbHistory.Count > PredictiveHistorySize)
            _availMbHistory.Dequeue();

        if (_availMbHistory.Count < PredictiveMinSamples) return;

        var samples = _availMbHistory.ToArray();
        int mid     = samples.Length / 2;
        double firstHalf = samples.Take(mid).Average();
        double lastHalf  = samples.Skip(mid).Average();
        double dropPerCycle = (firstHalf - lastHalf) / mid; // positif = RAM qui baisse

        if (dropPerCycle < PredictiveDropThresholdMbCycle) return;

        _log.LogInformation(
            "[PRÉDICTIF] Tendance RAM à la baisse détectée : {D:F0} Mo/cycle → optimisation préventive",
            dropPerCycle);
        Console.WriteLine($"[RAM-AI] 🔮 PRÉDICTIF : RAM en baisse {dropPerCycle:F0} Mo/cycle → éviction préventive");

        // Éviction préventive des processus non-hot, non-système
        foreach (var p in allProcs)
        {
            try
            {
                if (SystemProcesses.Contains(p.ProcessName)) continue;
                float prob = Predict(p);
                if (prob > HotThreshold) continue;

                long freed = EvictProcess(p, prob, storeToColdCache: prob < ColdThreshold);
                if (freed >= 0) { coldEvicted++; mbSaved += freed; }
            }
            catch { }
        }
    }

    // ── Ultra : Profils gaming par jeu ────────────────────────────────────────

    private void LoadGamingProfilesIfNeeded()
    {
        if (!File.Exists(GamingProfilesPath))
        {
            // Créer un fichier de profils par défaut au premier lancement
            const string defaultJson =
                """
                {
                  "cs2":      { "intervalMs": 600,  "maxProcs": 10 },
                  "valorant": { "intervalMs": 700,  "maxProcs": 10 },
                  "fortnite": { "intervalMs": 800,  "maxProcs": 12 },
                  "eldenring":{ "intervalMs": 1000, "maxProcs": 15 },
                  "default":  { "intervalMs": 1200, "maxProcs": 20 }
                }
                """;
            try { File.WriteAllText(GamingProfilesPath, defaultJson); } catch { }
        }

        try
        {
            var fi = new FileInfo(GamingProfilesPath);
            if (fi.LastWriteTimeUtc <= _profilesLastLoaded) return;

            string json = File.ReadAllText(GamingProfilesPath);
            _gamingProfiles = System.Text.Json.JsonSerializer.Deserialize
                <Dictionary<string, GamingProfile>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _profilesLastLoaded = fi.LastWriteTimeUtc;
            _log.LogInformation("[Ultra] Gaming profiles chargés : {N} profils", _gamingProfiles?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Ultra] Erreur lecture gaming_profiles.json");
        }
    }

    private GamingProfile? GetGameProfile(string gameName)
    {
        if (_gamingProfiles is null) return null;
        if (_gamingProfiles.TryGetValue(gameName, out var p))   return p;
        if (_gamingProfiles.TryGetValue("default",  out var d)) return d;
        return null;
    }

    // ── Ultra : VRAM (mise à jour périodique via WMI) ─────────────────────────

    internal void RefreshVramInfo()
    {
        // NvAPI / DXGI requis pour l'usage temps-réel — WMI donne la VRAM totale.
        // Appelé tous les 60 ticks depuis le RunAsync loop.
        _vramMb = NativeMemory.GetTotalVramMb();
        if (_vramMb > 0)
            _log.LogInformation("[Ultra] VRAM adaptateur : {V} Mo", _vramMb);
    }

    private static bool IsAiProcess(string name) => KnownAiProcesses.Contains(name);

    // ── Détection et application du mode Éco (batterie) ─────────────────────

    private void RefreshEcoMode()
    {
        bool onBattery = NativeMemory.IsOnBattery();

        // Log de diagnostic à chaque vérification (pas seulement au changement d'état)
        Console.WriteLine($"[ECO] RefreshEcoMode — sur batterie : {onBattery} | écoMode courant : {_ecoMode}");
        _log.LogInformation("[ECO] RefreshEcoMode — sur batterie : {B} | écoMode courant : {E}",
            onBattery, _ecoMode);

        if (onBattery == _ecoMode) return; // pas de changement d'état

        _ecoMode = onBattery;
        UpdateInterval(); // recalcule _intervalMs avec les valeurs éco ou normales

        if (_ecoMode)
        {
            _log.LogInformation(
                "[ÉCO] Batterie détectée → mode éco ON " +
                "(intervalle={I}ms, max {M} procs/cycle, sleep={S}ms/batch)",
                _intervalMs, MaxProcsPerCycle, BatchSleepMs);
            Console.WriteLine($"[RAM-AI] 🔋 MODE ÉCO ON (intervalle={_intervalMs}ms, max {MaxProcsPerCycle} procs/cycle)");
            _events.WriteMarker("ECO MODE ON — batterie");
        }
        else
        {
            _log.LogInformation(
                "[ÉCO] Secteur détecté → mode éco OFF (intervalle={I}ms)", _intervalMs);
            Console.WriteLine($"[RAM-AI] ⚡ MODE ÉCO OFF — secteur (intervalle={_intervalMs}ms)");
            _events.WriteMarker("ECO MODE OFF — secteur");
        }
    }

    /// <summary>
    /// Lit la Working Set d'un processus via GetProcessMemoryInfo (P/Invoke).
    /// Retourne 0 si le handle ne peut pas être ouvert ou la lecture échoue.
    /// </summary>
    private static long ReadWorkingSetMb(int pid)
    {
        IntPtr h = NativeMemory.OpenProcess(NativeMemory.PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0L;
        try
        {
            uint cb = (uint)Marshal.SizeOf<NativeMemory.PROCESS_MEMORY_COUNTERS_EX>();
            return NativeMemory.GetProcessMemoryInfo(h, out var cnt, cb)
                ? (long)cnt.WorkingSetSize / (1024L * 1024L)
                : 0L;
        }
        finally { NativeMemory.CloseHandle(h); }
    }

    /// <summary>
    /// Retourne true si le processus est inactif (CPU approx < 2% depuis le dernier tick).
    /// Met aussi à jour le suivi CPU.
    /// </summary>
    private bool IsAiProcessInactive(Process proc)
    {
        try
        {
            TimeSpan currentCpu = proc.TotalProcessorTime;
            bool hadPrev = _prevCpuTime.TryGetValue(proc.Id, out TimeSpan prevCpu);
            _prevCpuTime[proc.Id] = currentCpu;

            if (!hadPrev) return true; // premier tick : pas de delta CPU → évincer d'emblée si WS dépasse le seuil

            double deltaMsActive = (currentCpu - prevCpu).TotalMilliseconds;
            double deltaMsElapsed = AiIntervalMs * Environment.ProcessorCount; // budget max
            double cpuPct = deltaMsActive / deltaMsElapsed * 100.0;

            return cpuPct < 2.0; // inactif si < 2% CPU
        }
        catch { return true; } // accès refusé CPU → traiter comme inactif, l'éviction sera tentée
    }

    private void UpdateCpuTracking(Process proc)
    {
        try { _prevCpuTime[proc.Id] = proc.TotalProcessorTime; }
        catch { }
    }

    // ── Détection navigateur ─────────────────────────────────────────────────

    private static (bool IsBrowser, string BrowserName) DetectBrowser(Process[] procs)
    {
        foreach (var p in procs)
        {
            try
            {
                if (KnownBrowserProcesses.Contains(p.ProcessName))
                    return (true, p.ProcessName);
            }
            catch { }
        }
        return (false, string.Empty);
    }

    private void ApplyBrowserMode(bool isBrowser, string browserName)
    {
        if (isBrowser && !_browserModeActive)
        {
            _browserModeActive = true;
            _currentBrowser    = browserName;
            UpdateInterval();
            _events.WriteMarker($"BROWSER MODE ON — {browserName}");
            _log.LogInformation(">>> Browser mode ON  : {B} (intervalle {I}ms)", browserName, BrowserIntervalMs);
            Console.WriteLine($"[RAM-AI] >>> BROWSER MODE ON  : {browserName} (intervalle={BrowserIntervalMs}ms)");
        }
        else if (!isBrowser && _browserModeActive)
        {
            _browserModeActive = false;
            _currentBrowser    = string.Empty;
            UpdateInterval();
            _events.WriteMarker("BROWSER MODE OFF");
            _log.LogInformation(">>> Browser mode OFF : intervalle {I}ms", _intervalMs);
            Console.WriteLine($"[RAM-AI] >>> BROWSER MODE OFF (intervalle={_intervalMs}ms)");
        }
    }

    // ── Mode gaming ──────────────────────────────────────────────────────────

    private void ApplyGamingMode(bool isGaming, string gameName)
    {
        if (isGaming && !_gamingModeActive)
        {
            _gamingModeActive = true;
            _currentGame      = gameName;
            UpdateInterval();

            // Écrire le flag "auto" pour notifier Phase4
            string existing = ReadFlagContent();
            if (existing != "manual")
            {
                try
                {
                    Directory.CreateDirectory(SharedFlagDir);
                    File.WriteAllText(ForceFlagPath, "auto");
                    Console.WriteLine($"[RAM-AI] Flag 'auto' écrit : {ForceFlagPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RAM-AI] ERREUR écriture flag : {ex.Message}");
                    _log.LogError(ex, "Impossible d'écrire le flag gaming_mode.force");
                }
            }

            _events.WriteMarker($"GAMING MODE ON — {gameName}");
            _log.LogInformation(">>> Gaming mode ON  : {G} | intervalle {I}ms (anti-freeze) | flag={F}",
                gameName, GamingIntervalMs, ForceFlagPath);
            Console.WriteLine($"[RAM-AI] >>> GAMING MODE ON  : {gameName}  (intervalle={GamingIntervalMs}ms, priorité BelowNormal, max 20 procs/cycle)");
        }
        else if (!isGaming && _gamingModeActive)
        {
            _gamingModeActive = false;
            _currentGame      = string.Empty;
            UpdateInterval();

            if (ReadFlagContent() == "auto")
            {
                try
                {
                    File.Delete(ForceFlagPath);
                    Console.WriteLine($"[RAM-AI] Flag 'auto' supprimé : {ForceFlagPath}");
                }
                catch { }
            }

            _events.WriteMarker("GAMING MODE OFF");
            _log.LogInformation(">>> Gaming mode OFF : intervalle {I}ms", _intervalMs);
            Console.WriteLine($"[RAM-AI] >>> GAMING MODE OFF (intervalle={_intervalMs}ms)");
        }
    }

    // ── Inférence ML (thread-safe via lock) ──────────────────────────────────

    private float Predict(Process proc)
    {
        // Sans modèle ML : retourner prob = 0 → tous les processus passent
        // par le cold path (éviction systématique, stockage en cache NVMe).
        if (_engine is null) return 0f;

        float wsMB   = (float)(proc.WorkingSet64        / (1024.0 * 1024.0));
        float privMB = (float)(proc.PrivateMemorySize64 / (1024.0 * 1024.0));
        float pfKB   = GetPageFaultDeltaKB(proc);
        float ratio  = privMB > 0f ? wsMB / privMB : 1f;
        float logWS  = wsMB  > 0f ? (float)Math.Log2(wsMB) : 0f;

        var input = new MlInputRow
        {
            WorkingSetMB     = wsMB,
            PrivateBytesMB   = privMB,
            PageFaultDeltaKB = Math.Min(pfKB, 100f * 1024f),
            WsToPrivateRatio = ratio,
            LogWorkingSet    = logWS,
        };

        // PredictionEngine n'est pas thread-safe
        lock (_predictLock)
            return _engine.Predict(input).Probability;
    }

    // ── Éviction standard (mode normal) ───────────────────────────────────────
    // Appliqué à TOUS les processus non-chauds (prob <= HotThreshold),
    // pas seulement aux processus froids.
    // storeToColdCache = true uniquement pour les processus vraiment froids.

    /// <summary>
    /// Évince le working set du processus et retourne les Mo réellement libérés
    /// (WS avant − WS après, mesuré via Refresh). Retourne -1 si l'opération échoue.
    /// </summary>
    private long EvictProcess(Process proc, float prob, bool storeToColdCache)
    {
        IntPtr hProc = NativeMemory.OpenProcess(
            NativeMemory.PROCESS_QUERY_INFORMATION | NativeMemory.PROCESS_SET_QUOTA,
            false, proc.Id);
        if (hProc == IntPtr.Zero) return -1L;

        try
        {
            uint cbSize = (uint)Marshal.SizeOf<NativeMemory.PROCESS_MEMORY_COUNTERS_EX>();

            // Lire WS avant via P/Invoke sur le handle ouvert (plus fiable que proc.WorkingSet64)
            if (!NativeMemory.GetProcessMemoryInfo(hProc, out var cntBefore, cbSize))
                return -1L;
            long wsBefore = (long)cntBefore.WorkingSetSize;

            if (storeToColdCache)
            {
                _cache.StoreColdSnapshot(new ColdProcessEntry
                {
                    Pid             = proc.Id,
                    Name            = proc.ProcessName,
                    CachedAt        = DateTime.UtcNow,
                    WorkingSetBytes = wsBefore,
                    PrivateBytes    = (long)cntBefore.PrivateUsage,
                    LastProbability = prob,
                });
            }

            // Double frappe : EmptyWorkingSet déplace les pages vers la liste standby,
            // SetProcessWorkingSetSizeEx(-1,-1) force un trim immédiat du working set.
            bool ok = NativeMemory.EmptyWorkingSet(hProc);
            NativeMemory.SetProcessWorkingSetSizeEx(
                hProc, new IntPtr(-1), new IntPtr(-1), 0);

            if (!ok)
            {
                _log.LogDebug("EmptyWorkingSet PID {P} err={E}", proc.Id, Marshal.GetLastWin32Error());
                return -1L;
            }

            // Relire WS après via P/Invoke
            if (!NativeMemory.GetProcessMemoryInfo(hProc, out var cntAfter, cbSize))
                return -1L;
            long wsAfter = (long)cntAfter.WorkingSetSize;
            long deltaMb = Math.Max(0L, (wsBefore - wsAfter) / (1024L * 1024L));

            _log.LogInformation("[RAM] Processus {N} : WS avant={B}Mo après={A}Mo delta={D}Mo",
                proc.ProcessName, wsBefore / (1024 * 1024), wsAfter / (1024 * 1024), deltaMb);

            return deltaMb;
        }
        finally { NativeMemory.CloseHandle(hProc); }
    }

    // ── Éviction Turbo (mode Turbo : tous les processus) ─────────────────────

    /// <summary>
    /// Éviction agressive sans stockage cache. Délègue à EvictProcess (prob=0, pas de cache).
    /// </summary>
    private long EvictTurbo(Process proc) =>
        EvictProcess(proc, 0f, storeToColdCache: false);

    // ── Helper Tournoi : Working Set du jeu actif ────────────────────────────

    /// <summary>
    /// Retourne le WorkingSet (en Mo) du premier processus correspondant au nom du jeu.
    /// Retourne 0 si introuvable ou accès refusé.
    /// </summary>
    private static long GetGameWorkingSetMb(string gameName)
    {
        if (string.IsNullOrEmpty(gameName)) return 0L;
        // _currentGame peut contenir des suffixes comme " (>1Go RAM)" ou "(dashboard)"
        // → n'utiliser que le premier token (le nom du processus réel)
        string procName = gameName.Split(' ')[0];
        try
        {
            var procs = Process.GetProcessesByName(procName);
            long ws = procs.Length > 0 ? procs[0].WorkingSet64 / (1024L * 1024L) : 0L;
            foreach (var p in procs) p.Dispose();
            return ws;
        }
        catch { return 0L; }
    }

    // ── Hot path ──────────────────────────────────────────────────────────────

    private int PrefetchHot(Process proc)
    {
        IntPtr hProc = NativeMemory.OpenProcess(
            NativeMemory.PROCESS_QUERY_INFORMATION | NativeMemory.PROCESS_VM_READ,
            false, proc.Id);
        if (hProc == IntPtr.Zero) return 0;

        try
        {
            IntPtr baseAddr = IntPtr.Zero;
            try { baseAddr = proc.MainModule?.BaseAddress ?? IntPtr.Zero; }
            catch { return 0; }
            if (baseAddr == IntPtr.Zero) return 0;

            var range = new NativeMemory.WIN32_MEMORY_RANGE_ENTRY
            {
                VirtualAddress = baseAddr,
                NumberOfBytes  = (UIntPtr)(ulong)proc.WorkingSet64,
            };

            NativeMemory.PrefetchVirtualMemory(hProc, (UIntPtr)1, [range], 0);
            return (int)(proc.WorkingSet64 / 4096);
        }
        finally { NativeMemory.CloseHandle(hProc); }
    }

    // ── Helper page faults (thread-safe via ConcurrentDictionary) ────────────

    private float GetPageFaultDeltaKB(Process proc)
    {
        IntPtr hProc = NativeMemory.OpenProcess(
            NativeMemory.PROCESS_QUERY_INFORMATION, false, proc.Id);
        if (hProc == IntPtr.Zero) return 0f;
        try
        {
            var mem = default(NativeMemory.PROCESS_MEMORY_COUNTERS_EX);
            mem.cb  = (uint)Marshal.SizeOf<NativeMemory.PROCESS_MEMORY_COUNTERS_EX>();
            if (!NativeMemory.GetProcessMemoryInfo(hProc, out mem, mem.cb)) return 0f;

            _prevFaults.TryGetValue(proc.Id, out uint prev);
            uint cur = mem.PageFaultCount;
            _prevFaults[proc.Id] = cur;

            uint delta = cur >= prev ? cur - prev : 0u;
            return delta * 4f;
        }
        finally { NativeMemory.CloseHandle(hProc); }
    }

    public void Dispose()
    {
        _engine?.Dispose();   // null si mode sans ML
        _cache.Dispose();
    }
}

// ── ML.NET row schemas ────────────────────────────────────────────────────────

internal sealed class MlInputRow
{
    [ColumnName("WorkingSetMB")]     public float WorkingSetMB     { get; init; }
    [ColumnName("PrivateBytesMB")]   public float PrivateBytesMB   { get; init; }
    [ColumnName("PageFaultDeltaKB")] public float PageFaultDeltaKB { get; init; }
    [ColumnName("WsToPrivateRatio")] public float WsToPrivateRatio { get; init; }
    [ColumnName("LogWorkingSet")]    public float LogWorkingSet    { get; init; }
    [ColumnName("Label")]            public bool  Label            { get; init; }
}

internal sealed class MlOutputRow
{
    [ColumnName("PredictedLabel")] public bool  WillAccessMemory { get; set; }
    [ColumnName("Probability")]    public float Probability       { get; set; }
    [ColumnName("Score")]          public float Score             { get; set; }
}

/// <summary>Profil d'optimisation gaming par jeu (lu depuis gaming_profiles.json).</summary>
internal sealed class GamingProfile
{
    public int IntervalMs { get; set; } = 1_200;
    public int MaxProcs   { get; set; } = 20;
}
