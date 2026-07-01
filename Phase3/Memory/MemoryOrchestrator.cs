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
    // ── Intervalles par palier de charge ──────────────────────────────────────
    private const int   ReposIntervalMs      = 3_000;  // CPU < 20% ET RAM < 60% → repos
    private const int   NormalIntervalMs     = 2_000;  // défaut (inclut zone 60-75%)
    private const int   HighRamIntervalMs    = 1_200;  // RAM > 75% utilisée
    private const int   SwapIntervalMs       =   500;  // Swap > 100 pages/sec (priorité absolue)
    private const int   GamingIntervalMs     = 1_200;  // mode Gaming

    // ── Seuils de charge ──────────────────────────────────────────────────────
    private const float CpuReposPct             =  20f;    // CPU < 20% (condition repos)
    private const float RamReposPct             = 0.60f;   // RAM < 60% utilisée (condition repos)

    // HighRam : seuil absolu en Mo disponibles — proportionnel à la RAM totale,
    // avec plancher pour les petites machines. Entrée < 15%, sortie > 22%.
    // Sur 16 Go : entrée < 2 444 Mo, sortie > 3 585 Mo.
    // Sur  8 Go : entrée < 2 000 Mo (plancher), sortie > 3 000 Mo (plancher).
    // Sur 32 Go : entrée < 4 915 Mo, sortie > 7 187 Mo.
    private const float HighRamAvailFraction     = 0.15f;  // entrer HighRam si avail < 15% total
    private const float HighRamExitAvailFraction = 0.22f;  // sortir HighRam si avail > 22% total
    private const long  HighRamAvailMinMb        = 2_000L; // plancher entrée (machines < 13 Go)
    private const long  HighRamExitMinMb         = 3_000L; // plancher sortie

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

    // ── Trim prédictif basé sur la pente d'availMb ────────────────────────────
    // Ring buffer de N ticks + pente moyenne → estime le temps avant franchissement HighRam.
    // Si projection < PredictiveTrimLeadTimeSec → éviction anticipée PREDICTIVE_TRIM.
    private const int   PredictiveTrimRingSize       = 15;
    // Fenêtre d'anticipation — à ajuster après analyse des logs PREDICTIVE_TRIM en session réelle.
    // Trop petit : déclenche trop tard (inutile). Trop grand : faux positifs sur pics courts.
    private const float PredictiveTrimLeadTimeSec    =  5f;
    // Inhibition post-déclenchement en ticks. À 1200ms (Gaming) = 12s de rebond.
    // À ajuster si les logs montrent des rafales (trop bas) ou des manques (trop haut).
    private const int   PredictiveTrimCooldownTicks  = 10;

    // ── GPU Non-Local : offset d'entrée HighRam si pression GPU montante ─────
    // Fraction d'élargissement du seuil d'entrée HighRam quand GPU pressure est détectée.
    // Ex. 0.15 → seuil 2444 Mo passe à 2811 Mo sur 16 Go (HighRam déclenche 15% plus tôt).
    // À ajuster après tests si trop de faux positifs ou trop peu d'anticipation.
    private const float GpuPressureOffsetFraction    = 0.15f;

    // ── Heuristique gaming Niveau 3 — hystérèse et seuils ────────────────────
    // Entrée : signaux (plein écran + GPU 3D) continus pendant HeuristicEnterSeconds.
    // 12 s choisi pour éviter les faux positifs sur alt-tab/chargement tout en restant
    // réactif (≈ 10 ticks à 1200 ms, le palier Gaming). Ajustable après retours testeurs.
    private const int   HeuristicEnterSeconds      = 12;
    // Sortie : grâce de 5 s après perte de signal (alt-tab bref, écran de chargement).
    // Symétrie avec l'hystérèse HighRam (entrée ≠ sortie) — évite le flapping.
    private const int   HeuristicExitSeconds       = 5;
    // Seuil GPU Engine 3D minimum (sum des instances 3D du PID, en %). Empirique.
    // 20% élimine l'activité 3D de fond (curseur, DWM) tout en restant sous le charge
    // d'un jeu vrai (typiquement 50-100%). À ajuster si faux positifs signalés.
    private const float HeuristicGpu3DThresholdPct = 20f;
    // Tolérance bord d'écran pour les fenêtres quasi-plein-écran sans bordure.
    // ± 8 px couvre les barres de défilement et les titres cachés sans faux positifs.
    private const int   ScreenEdgeTolerancePx      = 8;

    // ── Intervalles et limites mode Éco ──────────────────────────────────────
    private const int   EcoIntervalMs        = 3_000;
    private const int   EcoMaxProcsPerCycle  =     8;  // max processus par cycle en mode éco
    private const int   EcoBatchSleepMs      =   100;  // pause entre batches en mode éco
    private const int   NormalBatchSleepMs   =    50;  // pause entre batches en mode normal

    // ── Limites de processus par palier ───────────────────────────────────────
    private const int   ReposMaxProcs        =  10;
    private const int   NormalMaxProcs       =  15;
    private const int   HighRamMaxProcs      =  20;

    // ── Pré-filtre WS avant inférence ML ─────────────────────────────────────
    // Processus dont le WS est inférieur à ce seuil ne valent pas le coût d'un
    // appel ML (~6-8ms) : même si on les évince, le gain mémoire est négligeable.
    // Traités directement comme prob=0 (cold path). Empirique, ajustable.
    private const float MinWorkingSetMbForPredict = 10f;

    // ── Cooldown par PID entre deux évictions ─────────────────────────────────
    // Durée minimale avant de réévincer le même processus (même PID + même StartTime).
    // Empirique — trop bas : réévictions en boucle sous pression (cause du 21% CPU observé) ;
    // trop haut : processus rapide à reconstruire son WS n'est pas retrimmé assez souvent.
    // À ajuster si les logs montrent skippedCooldown trop élevé (>80% seen) ou trop bas (<20%).
    private const double EvictionCooldownSeconds = 2.5;

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
    private static readonly string EcoFlagPath        = Path.Combine(SharedFlagDir, "eco_mode.force");
    private static readonly string GamingProfilesPath = Path.Combine(SharedFlagDir, "gaming_profiles.json");
    // Écrit toutes les 500 ms par Phase4 (session interactive) — Phase3 (Session 0) lit ici
    // pour connaître la fenêtre au premier plan sans appeler GetForegroundWindow() directement.
    private static readonly string ForegroundPath     = Path.Combine(SharedFlagDir, "foreground.json");

    // ── État Ultra ────────────────────────────────────────────────────────────
    private bool     _tournamentModeActive;
    private DateTime _tournamentSuspendUntil  = DateTime.MinValue; // suspension 2s anti-stutter
    private DateTime _stutterWsFirstTrigger   = DateTime.MinValue; // horloge anti-boucle infinie stutter-ws
    private const int TournamentMaxStutterBlockMs = 10_000;        // bypass forcé après 10s de blocage consécutif
    private long     _vramMb;   // VRAM totale de l'adaptateur (WMI, mise à jour périodique)

    // ── Optimisation prédictive — historique RAM disponible ──────────────────
    private readonly Queue<long> _availMbHistory = new();

    // ── Détection précoce de chute availMb (observation seule) ───────────────
    // Ring buffer de 4 slots : 3 deltas consécutifs suffisent pour confirmer la tendance.
    // Aucune action — log [PRESSWAP-DBG] uniquement, pour valider l'hypothèse sur plusieurs jours.
    private const long  PreSwapDropThresholdMb   = 800L;  // chute > 800 Mo/tick = signal
    private const int   PreSwapConsecTicks        = 3;     // 3 ticks consécutifs requis
    private const int   PreSwapCooldownTicks      = 5;     // ticks d'inhibition après HighRam/AntiSwap
    private readonly long[] _availMbRing          = new long[4]; // [i%4] = availMb au tick i
    private int         _availMbRingIdx;                   // index courant dans le ring
    private int         _preSwapCooldownRemaining;         // ticks restants de cooldown

    // ── Emergency bypass : court-circuit one-shot du cooldown per-PID ────────
    // Déclenché par une chute RAM > EmergencyBypassRamDropMb sur un seul tick,
    // uniquement en mode Tournoi ou AntiSwap actif.
    // Durée exacte : EmergencyBypassTicks × intervalMs (~4×500ms = 2s).
    // Réarmement inhibé EmergencyBypassRearmDelayMs (30s) après fin du bypass
    // pour éviter de reproduire le pattern CPU 21,77% (commits 904ced8/b03f1d5).
    private const long EmergencyBypassRamDropMb    = 1500L;  // chute > 1500 Mo/tick = signal
    private const int  EmergencyBypassTicks         = 4;      // durée = 4 ticks (~2s à 500ms)
    private const long EmergencyBypassRearmDelayMs  = 30_000L; // fenêtre d'inhibition post-bypass

    // ── Ring buffer trim prédictif ────────────────────────────────────────────
    private readonly long[] _predictRing         = new long[PredictiveTrimRingSize];
    private int              _predictRingIdx;
    private int              _predictSampleCount;
    private int              _predictiveTrimCooldown; // ticks d'inhibition restants

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
            "cs2", "csgo",
            "valorant", "VALORANT-Win64-Shipping",  // exe jeu Riot — le launcher est dans LauncherBlacklist
            "fortnite",
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
            // NOTE : les launchers (Steam, Epic, Riot, EA…) sont dans LauncherBlacklist — jamais ici.
        };

    // ── Niveau 1 : launchers blacklistés — jamais classés Gaming, lookup O(1) ──
    // Process.ProcessName ne contient jamais l'extension .exe sur Windows.
    private static readonly HashSet<string> LauncherBlacklist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Steam
            "steam", "steamwebhelper", "gameoverlayui",
            // Epic Games
            "epicgameslauncher", "fortnitelauncher",
            // Origin / EA App
            "origin", "eadesktop", "eaapp", "eabackgroundservice",
            // Ubisoft Connect
            "ubisoftconnect", "uplay",
            // Battle.net (Blizzard)
            "battlenet",
            // GOG Galaxy
            "gogalaxy",
            // Riot (launcher + lobby League — VALORANT-Win64-Shipping est le JEU, non blacklisté)
            "riotclientservices", "riotclient", "leagueclient",
            // Xbox App / Microsoft Gaming Services
            "xboxapp", "gamingservices", "gamingservicesnet",
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

    // ── Heuristique gaming Niveau 3 ────────────────────────────────────────────
    private DateTime _heuristicSignalSince  = DateTime.MinValue; // premier tick avec signaux continus
    private DateTime _heuristicLostSince    = DateTime.MinValue; // premier tick sans signal après activation
    private bool     _heuristicActive;                           // true = Gaming détecté heuristiquement
    private int      _heuristicFgPid        = -1;                // PID pour lequel les compteurs GPU sont cachés
    private readonly List<PerformanceCounter> _gpuEngine3DCounters = new();
    private bool     _gpuEngineAvailable    = true;              // false si catégorie GPU Engine absente (VM, vieux drivers)

    // ── Mode Éco (batterie) ───────────────────────────────────────────────────
    private bool _ecoMode;

    // ── Intervalle, limites et charge (recalculés chaque tick par UpdateInterval) ──
    private int   _intervalMs       = NormalIntervalMs;
    private int   _maxProcsPerCycle = NormalMaxProcs;
    private int   _batchSleepMs     = NormalBatchSleepMs;
    private float _cpuPct;
    private float _ramUsedPct;
    private long  _tickAvailMb;          // Mo disponibles ce tick (source UpdateInterval)
    private long  _highRamThresholdMb;   // calculé une fois au premier tick depuis totalRamMb
    private long  _highRamExitMb;        // idem — seuil de sortie d'hystérèse
    // Hystérèse HighRam : évite d'osciller entre paliers quand avail ≈ seuil
    private bool  _highRamActive;        // true = entré en HighRam, sort seulement si avail > _highRamExitMb

    // ── CPU counter (paliers d'intervalle) ────────────────────────────────────
    private readonly PerformanceCounter? _cpuCounter;

    // ── Protection processus ──────────────────────────────────────────────────
    private int _foregroundPid = -1;
    private readonly ConcurrentDictionary<int, (long WsMb, long TickMs)> _wsTracker = new();

    // ── Cooldown par PID : évite les réévictions en boucle ───────────────────
    // Clé = PID ; valeur = (ms de la dernière éviction, ticks StartTime du processus).
    // StartTimeTicks permet de détecter le recyclage de PID par Windows.
    // Thread-safe : ConcurrentDictionary car utilisé depuis Parallel.ForEach (AntiSwap).
    private readonly ConcurrentDictionary<int, (long LastEvictedMs, long StartTimeTicks)> _evictionCooldown = new();
    // Compteur de skips par tick (reset au début de Tick, lu dans [PERF-TICK]).
    // Interlocked pour thread-safety dans Parallel.ForEach.
    private int _tickSkippedCooldown;

    // ── État du bypass d'urgence ──────────────────────────────────────────────
    // volatile : _emergencyBypassTicksRemaining est lu depuis le Parallel.ForEach
    // (EvictProcess) et écrit depuis le tick principal — volatile garantit la visibilité.
    private volatile int _emergencyBypassTicksRemaining; // 0 = bypass inactif
    private int          _emergencyBypassEvictedCount;   // évictions pendant la fenêtre (Interlocked)
    private long         _emergencyBypassStartMs;        // TickCount64 à l'armement (log durée)
    private long         _emergencyBypassLastEndMs;      // TickCount64 à la fin du dernier bypass (réarmement)

    /// <summary>
    /// Recalcule l'intervalle et les limites par palier de charge.
    /// Priorité : Tournoi > Éco > Gaming > Swap > HighRam(hyst) > Med > Repos > Normal.
    /// AntiSwap a priorité absolue — un pic de swap n'est jamais retardé par l'hystérèse RAM.
    /// HighRam utilise une hystérèse : entrée à >75%, sortie seulement à <65%.
    /// Repos nécessite CPU<20% ET RAM<60% simultanément (évite repos pendant pression mémoire).
    /// </summary>
    private void UpdateInterval()
    {
        // Tournoi, Éco, Gaming : overrides totaux, non affectés par les seuils RAM/CPU
        if (_tournamentModeActive)
        {
            _intervalMs       = TournamentIntervalMs;
            _maxProcsPerCycle = int.MaxValue;
            _batchSleepMs     = 0;
            return;
        }
        if (_ecoMode)
        {
            _intervalMs       = EcoIntervalMs;
            _maxProcsPerCycle = EcoMaxProcsPerCycle;
            _batchSleepMs     = EcoBatchSleepMs;
            return;
        }
        if (_gamingModeActive)
        {
            _intervalMs       = GamingIntervalMs;
            _maxProcsPerCycle = HighRamMaxProcs;
            _batchSleepMs     = NormalBatchSleepMs;
            return;
        }

        // AntiSwap : priorité absolue sur tous les paliers RAM, y compris hystérèse active.
        // Un pic de swap soudain ne doit jamais être retardé par l'état _highRamActive.
        if (AntiSwapActive)
        {
            _intervalMs       = SwapIntervalMs;
            _maxProcsPerCycle = int.MaxValue;
            _batchSleepMs     = 0;
            return;
        }

        // Hystérèse HighRam : entrée si avail < seuil (15% total), sortie si avail > seuil sortie (22% total).
        // Basé sur Mo disponibles absolus — robuste sur 8/16/32 Go, insensible à la standby list.
        // GPU pressure : si VRAM partagée monte vite (>25 Mo/s, normalisé), abaisser légèrement
        // le seuil d'entrée HighRam (= déclencher plus tôt pour compenser la pression GPU imminente).
        long effectiveHighRamThreshold = _highRamThresholdMb;
        if (_gpuMonitor.IsGpuMemoryPressureRising(_intervalMs))
            effectiveHighRamThreshold = (long)(_highRamThresholdMb * (1f + GpuPressureOffsetFraction));

        if (_tickAvailMb < effectiveHighRamThreshold)
            _highRamActive = true;
        else if (_tickAvailMb > _highRamExitMb)
            _highRamActive = false;
        // Entre les deux seuils : _highRamActive reste à sa valeur précédente (zone d'hystérèse)

        if (_highRamActive)
        {
            _intervalMs       = HighRamIntervalMs;
            _maxProcsPerCycle = HighRamMaxProcs;
            _batchSleepMs     = NormalBatchSleepMs;
            return;
        }

        // Repos : CPU bas ET RAM confortable (les deux conditions requises)
        if (_cpuPct < CpuReposPct && _ramUsedPct < RamReposPct)
        {
            _intervalMs       = ReposIntervalMs;
            _maxProcsPerCycle = ReposMaxProcs;
            _batchSleepMs     = NormalBatchSleepMs;
            return;
        }

        _intervalMs       = NormalIntervalMs;
        _maxProcsPerCycle = NormalMaxProcs;
        _batchSleepMs     = NormalBatchSleepMs;
    }

    // ── Dépendances ───────────────────────────────────────────────────────────
    private readonly ILogger<MemoryOrchestrator>                _log;
    private readonly PageCacheManager                           _cache;
    private readonly EventLogger                                _events;
    private readonly MLContext?                                 _mlCtx;
    // Nullable : null si le modèle Phase2 est absent (mode sans ML)
    private readonly ITransformer?                               _mlModel;    // pour l'inférence par lot (batch)
    private readonly PredictionEngine<MlInputRow, MlOutputRow>? _engine;     // pour les appels unitaires (gaming, prédictif)
    private readonly object                                     _predictLock = new();
    // ConcurrentDictionary pour accès thread-safe depuis Parallel.ForEach
    private readonly ConcurrentDictionary<int, uint>            _prevFaults  = new();

    // ── Anti-Swap ─────────────────────────────────────────────────────────────
    private readonly PerformanceCounter? _swapCounter;
    public float SwapPagesPerSec { get; private set; }
    public bool  AntiSwapActive  { get; private set; }

    // ── Signal mémoire natif (observation) ───────────────────────────────────
    // Windows expose la standby list en trois counters séparés (Reserve + Normal + Core).
    // vraiePressionPct = (Total - Available - Standby) / Total mesure la pression
    // applicative réelle, sans compter le cache que Windows vide en <1ms si besoin.
    private readonly PerformanceCounter? _standbyReserveCounter;
    private readonly PerformanceCounter? _standbyNormalCounter;
    private readonly PerformanceCounter? _standbyCoreCounter;
    private long  _standbyMb;         // Mo en standby list (observation seule)
    private float _vraiePressionPct;  // signal de pression réelle (observation seule)

    // ── Monitoring GPU Non-Local ──────────────────────────────────────────────
    private readonly GpuMemoryMonitor _gpuMonitor;

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
            _mlModel  = model; // conservé pour l'inférence par lot dans le chemin AntiSwap/Normal
            _engine   = _mlCtx.Model.CreatePredictionEngine<MlInputRow, MlOutputRow>(model);
            _log.LogInformation("Model loaded — prédiction ML active (batch + engine).");
        }
        else
        {
            _log.LogWarning("Mode sans ML : prédiction désactivée. " +
                            "Gaming, Turbo et éviction restent opérationnels.");
            Console.WriteLine("[Phase3] Mode sans ML : tous les processus seront évincés (prob=0).");
        }

        try
        {
            _swapCounter = new PerformanceCounter("Memory", "Pages/sec", readOnly: true);
            _swapCounter.NextValue(); // premier appel toujours 0 — à jeter
        }
        catch (Exception ex)
        {
            _log.LogWarning("PerformanceCounter Pages/sec indisponible : {M}", ex.Message);
            _swapCounter = null;
        }

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // premier appel toujours 0 — à jeter
        }
        catch (Exception ex)
        {
            _log.LogWarning("PerformanceCounter CPU indisponible : {M}", ex.Message);
            _cpuCounter = null;
        }

        _standbyReserveCounter = TryCreateCounter("Memory", "Standby Cache Reserve Bytes");
        _standbyNormalCounter  = TryCreateCounter("Memory", "Standby Cache Normal Priority Bytes");
        _standbyCoreCounter    = TryCreateCounter("Memory", "Standby Cache Core Bytes");

        _gpuMonitor = new GpuMemoryMonitor(log);
        // WriteMarker → events.log (ILogger seul n'y écrit pas — même bug que PERF-TICK).
        // Un seul appel au démarrage, pas répété par tick.
        if (_gpuMonitor.IsAvailable)
            _events.WriteMarker(
                $"[GPU] Monitoring GPU Non-Local : OK — {_gpuMonitor.ActiveCounterCount} compteur(s) 'GPU Process Memory'");
        else
            _events.WriteMarker(
                "[GPU] Monitoring GPU Non-Local : désactivé — catégorie 'GPU Process Memory' absente (VM ou drivers anciens sans WDDM 2.x)");
    }

    private PerformanceCounter? TryCreateCounter(string category, string counter)
    {
        try
        {
            var pc = new PerformanceCounter(category, counter, readOnly: true);
            pc.NextValue(); // premier appel toujours 0 — à jeter
            return pc;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PerformanceCounter '{C}' indisponible : {M}", counter, ex.Message);
            return null;
        }
    }

    // ── Pont Phase4 → Phase3 pour la fenêtre au premier plan ───────────────────
    // Phase3 tourne en Session 0 (Windows Service) : GetForegroundWindow() appelé
    // depuis Session 0 retourne toujours IntPtr.Zero (isolement Session 0 depuis Vista).
    // Phase4 (session interactive) écrit foreground.json toutes les 500 ms avec :
    //   pid, name, isFullscreen, winW, winH, scrW, scrH, timestamp (ISO 8601 UTC)
    // Phase3 lit ce fichier à chaque tick au lieu d'appeler GetForegroundWindow().
    // Fallback si Phase4 fermé ou fichier périmé (>5 s) : pid=-1, tout vide/false.
    private (int Pid, string Name, bool IsFullscreen, int WinW, int WinH, int ScrW, int ScrH) ReadForegroundInfo()
    {
        try
        {
            if (!File.Exists(ForegroundPath)) return (-1, "", false, 0, 0, 0, 0);
            string json = File.ReadAllText(ForegroundPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Données périmées si > 5 s (Phase4 écrit toutes les 500 ms)
            if (root.TryGetProperty("timestamp", out var tsEl) &&
                DateTime.TryParse(tsEl.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime ts) &&
                (DateTime.UtcNow - ts).TotalSeconds > 5.0)
                return (-1, "", false, 0, 0, 0, 0);

            int    pid   = root.TryGetProperty("pid",         out var pe)  ? pe.GetInt32()        : -1;
            string name  = root.TryGetProperty("name",        out var ne)  ? ne.GetString() ?? "" : "";
            bool   fs    = root.TryGetProperty("isFullscreen",out var fe)  ? fe.GetBoolean()      : false;
            int    winW  = root.TryGetProperty("winW",        out var wwe) ? wwe.GetInt32()        : 0;
            int    winH  = root.TryGetProperty("winH",        out var whe) ? whe.GetInt32()        : 0;
            int    scrW  = root.TryGetProperty("scrW",        out var swe) ? swe.GetInt32()        : 0;
            int    scrH  = root.TryGetProperty("scrH",        out var she) ? she.GetInt32()        : 0;
            return (pid, name, fs, winW, winH, scrW, scrH);
        }
        catch { return (-1, "", false, 0, 0, 0, 0); }
    }

    // ── Boucle principale ─────────────────────────────────────────────────────

    internal async Task RunAsync(CancellationToken ct)
    {
        // Créer le dossier partagé si nécessaire (écrit et lu par Phase4 aussi)
        Directory.CreateDirectory(SharedFlagDir);
        _log.LogInformation("SharedFlagDir : {D}", SharedFlagDir);
        _log.LogInformation("ForceFlagPath : {F}", ForceFlagPath);
        _log.LogInformation("TurboFlagPath : {T}", TurboFlagPath);

        // Forcer le démarrage en mode Auto : supprimer les flags de modes manuels
        // résiduels d'une session précédente. L'utilisateur doit réactiver chaque
        // mode manuellement dans la nouvelle session — ils ne doivent jamais être
        // restaurés automatiquement au boot du service.
        foreach (var flagPath in new[] { TournamentFlagPath, TurboFlagPath, ForceFlagPath, EcoFlagPath })
        {
            if (!File.Exists(flagPath)) continue;
            try
            {
                File.Delete(flagPath);
                _log.LogInformation("[INIT] Flag supprimé au démarrage : {F}", flagPath);
                _events.WriteMarker($"STARTUP RESET — {Path.GetFileName(flagPath)} supprimé, démarrage en Auto");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INIT] Impossible de supprimer {F} : {M}", flagPath, ex.Message);
            }
        }

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

            // Mettre à jour la VRAM + purger _wsTracker + rafraîchir compteurs GPU toutes les 60 ticks (~1-3 min)
            if (_tickCount % 60 == 0)
            {
                RefreshVramInfo();
                PurgeWsTracker();
                PurgeEvictionCooldown();
                _gpuMonitor.RefreshCounters();
            }

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
        _tickSkippedCooldown    = 0; // reset avant le cycle — lu dans [PERF-TICK] en fin de tick

        var swEnum = Stopwatch.StartNew();
        Process[] allProcs = Process.GetProcesses();
        swEnum.Stop();
        long perfEnumMs = swEnum.ElapsedMilliseconds;

        // ── 1. Détection gaming ───────────────────────────────────────────────
        var (isGaming, gameName) = DetectGaming(allProcs);
        ApplyGamingMode(isGaming, gameName);

        // ── Anti-Swap : lecture Pages/sec + calcul agressivité ──────────────────
        bool antiSwapIntervention = false;
        if (_swapCounter is not null)
        {
            try { SwapPagesPerSec = _swapCounter.NextValue(); }
            catch { SwapPagesPerSec = 0f; }
        }
        // Vert < 10, Orange 10-100, Rouge > 100 → intervention
        AntiSwapActive = SwapPagesPerSec > 100f;
        if (AntiSwapActive) antiSwapIntervention = true;

        float coldThreshold = _gamingModeActive ? ColdThresholdGaming :
                              antiSwapIntervention ? Math.Min(ColdThreshold + 0.15f, 0.45f) :
                                                     ColdThreshold;

        // ── Lecture CPU + % RAM utilisée → UpdateInterval() ──────────────────────
        if (_cpuCounter is not null)
        {
            try { _cpuPct = _cpuCounter.NextValue(); }
            catch { _cpuPct = 30f; }
        }
        long tickTotalMb, tickAvailMb;
        {
            tickTotalMb    = NativeMemory.GetTotalPhysicalMb();
            tickAvailMb    = NativeMemory.GetAvailablePhysicalMb();
            _tickAvailMb   = tickAvailMb;
            _ramUsedPct    = tickTotalMb > 0 ? (float)(tickTotalMb - tickAvailMb) / tickTotalMb : 0f;
            // Initialisation unique des seuils HighRam (dépend de la RAM totale de la machine)
            if (_highRamThresholdMb == 0 && tickTotalMb > 0)
            {
                _highRamThresholdMb = Math.Max(HighRamAvailMinMb, (long)(tickTotalMb * HighRamAvailFraction));
                _highRamExitMb      = Math.Max(HighRamExitMinMb,  (long)(tickTotalMb * HighRamExitAvailFraction));
                _log.LogInformation(
                    "[MEM-SIGNAL] Seuils HighRam calculés : entrée<{E}Mo, sortie>{X}Mo (total={T}Mo)",
                    _highRamThresholdMb, _highRamExitMb, tickTotalMb);
            }
        }
        {
            static long ReadMb(PerformanceCounter? pc) {
                if (pc is null) return 0L;
                try { return (long)(pc.NextValue() / (1024f * 1024f)); }
                catch { return 0L; }
            }
            _standbyMb = ReadMb(_standbyReserveCounter)
                       + ReadMb(_standbyNormalCounter)
                       + ReadMb(_standbyCoreCounter);
        }
        long committedMb = tickTotalMb - tickAvailMb - _standbyMb;
        if (committedMb < 0) committedMb = 0;
        _vraiePressionPct = tickTotalMb > 0 ? (float)committedMb / tickTotalMb : 0f;

        // Lire GPU Non-Local avant UpdateInterval() pour que IsGpuMemoryPressureRising() soit à jour
        _gpuMonitor.Sample();

        UpdateInterval();

        // ── Détection précoce chute availMb (observation seule) ──────────────
        // Alimenter le ring buffer et décrémenter le cooldown chaque tick.
        // Réarmer le cooldown si on vient de sortir d'un palier HighRam ou AntiSwap
        // (pour éviter le faux positif de rebond post-éviction).
        _availMbRing[_availMbRingIdx % 4] = tickAvailMb;
        _availMbRingIdx++;
        if (_highRamActive || AntiSwapActive)
            _preSwapCooldownRemaining = PreSwapCooldownTicks;
        else if (_preSwapCooldownRemaining > 0)
            _preSwapCooldownRemaining--;
        CheckPreSwapDrop(tickAvailMb);

        // ── Décompte du bypass d'urgence ; log expiration quand il atteint 0 ──
        if (_emergencyBypassTicksRemaining > 0)
        {
            _emergencyBypassTicksRemaining--;
            if (_emergencyBypassTicksRemaining == 0)
            {
                long durationMs = Environment.TickCount64 - _emergencyBypassStartMs;
                _emergencyBypassLastEndMs = Environment.TickCount64;
                _events.WriteMarker(
                    $"[EMERGENCY-BYPASS-EXPIRED] duration={durationMs}ms evictedDuringBypass={_emergencyBypassEvictedCount}");
            }
        }

        CheckPredictiveTrim(tickAvailMb, allProcs, ref coldEvicted, ref mbSaved);

        // ── 3. Mode Tournoi (Ultra) — appliqué en priorité si gaming actif ────
        bool tournamentMode = File.Exists(TournamentFlagPath);
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
            // ── Instrumentation perf [PERF-TICK] ─────────────────────────────
            long perfPredictMs = 0L, perfEvictMs = 0L;
            int  perfSeen = 0, perfEvicted = 0;

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
            //    Mode normal  : limité à _maxProcsPerCycle + profil par jeu
            LoadGamingProfilesIfNeeded();
            var profile  = GetGameProfile(_currentGame);
            int maxProcs = _tournamentModeActive ? int.MaxValue
                         : (profile?.MaxProcs ?? _maxProcsPerCycle);
            int sleepMs  = _tournamentModeActive ? 0 : _batchSleepMs;

            if (profile is not null && !_tournamentModeActive)
                _log.LogInformation("[Ultra] Profil jeu '{G}' : maxProcs={M}", _currentGame, maxProcs);

            // ── Vérification seuil RAM pour le mode Tournoi ──────────────────
            bool   skipTournamentEviction = false;
            long   tournamentCapMb        = long.MaxValue; // cap de libération par cycle
            int    rawOtherCount          = otherProcs.Count; // capturé avant tout Clear() pour le log PERF-TICK
            string skipReason             = "none";

            if (_tournamentModeActive)
            {
                long totalMb   = NativeMemory.GetTotalPhysicalMb();
                long availMb   = NativeMemory.GetAvailablePhysicalMb();
                float availPct = totalMb > 0 ? (float)availMb / totalMb : 0f;

                // ── Suspension anti-stutter 2s : jeu WS > 80% RAM dispo ─────────
                if (DateTime.UtcNow < _tournamentSuspendUntil)
                {
                    // Vérifier si la protection anti-stutter bloque depuis trop longtemps :
                    // si la boucle stutter-ws → suspend-2s → stutter-ws tourne sans fin
                    // (rien n'est évincé donc la condition ne change jamais), forcer un bypass
                    // après TournamentMaxStutterBlockMs pour qu'au moins un cycle d'éviction passe.
                    bool timedOut = _stutterWsFirstTrigger != DateTime.MinValue
                        && (DateTime.UtcNow - _stutterWsFirstTrigger).TotalMilliseconds > TournamentMaxStutterBlockMs;

                    if (timedOut)
                    {
                        _tournamentSuspendUntil = DateTime.MinValue;
                        _stutterWsFirstTrigger  = DateTime.MinValue;
                        _log.LogWarning("[TOURNOI] Anti-stutter contourné après {M}s de blocage — forçage éviction",
                            TournamentMaxStutterBlockMs / 1000);
                        Console.WriteLine($"[RAM-AI] 🏆 Tournoi: bypass anti-stutter (>{TournamentMaxStutterBlockMs/1000}s) → éviction forcée");
                        _events.WriteMarker($"TOURNAMENT STUTTER-BYPASS — blocage >{TournamentMaxStutterBlockMs/1000}s, éviction forcée ce cycle");
                        // skipTournamentEviction reste false → ce cycle évince normalement
                    }
                    else
                    {
                        _log.LogDebug("[TOURNOI] Cycle suspendu (anti-stutter 2s actif)");
                        Console.WriteLine($"[RAM-AI] 🏆 Tournoi: cycle suspendu (anti-stutter 2s)");
                        foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                        otherProcs.Clear();
                        skipTournamentEviction = true;
                        skipReason = "suspend-2s";
                    }
                }
                else
                {
                    long gameWsMb = GetGameWorkingSetMb(_currentGame);
                    if (availMb > 0L && gameWsMb > (long)(availMb * TournamentGameRamPct))
                    {
                        // Mémoriser le premier déclenchement pour détecter une boucle infinie
                        if (_stutterWsFirstTrigger == DateTime.MinValue)
                            _stutterWsFirstTrigger = DateTime.UtcNow;

                        _tournamentSuspendUntil = DateTime.UtcNow.AddMilliseconds(TournamentSuspendMs);
                        _log.LogInformation(
                            "[TOURNOI] Jeu WS={W}Mo > 80% RAM dispo ({A}Mo) → suspension {S}ms",
                            gameWsMb, availMb, TournamentSuspendMs);
                        Console.WriteLine(
                            $"[RAM-AI] 🏆 Tournoi: jeu {gameWsMb}Mo > 80% RAM dispo ({availMb}Mo) → suspension 2s");
                        foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                        otherProcs.Clear();
                        skipTournamentEviction = true;
                        skipReason = "stutter-ws";
                    }
                    else
                    {
                        // Condition disparue (ou jamais présente ce cycle) → réinitialiser le compteur
                        _stutterWsFirstTrigger = DateTime.MinValue;
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
                    skipReason = "turbo-urgence";
                }
                else if (availPct >= TournamentRamThresholdPct)
                {
                    // RAM suffisante (≥ 25%) : pas besoin d'agir → zéro stutter
                    _log.LogDebug("[TOURNOI] RAM dispo {P:P0} ≥ {T:P0} — cycle sauté", availPct, TournamentRamThresholdPct);
                    Console.WriteLine($"[RAM-AI] 🏆 Tournoi: RAM {availPct:P0} OK — cycle sauté (anti-stutter)");
                    foreach (var dp in otherProcs) try { dp.Dispose(); } catch { }
                    otherProcs.Clear();
                    skipTournamentEviction = true;
                    skipReason = $"ram-ok({availPct:P0})";
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

                        perfSeen++;
                        proc.Refresh();
                        var swP = Stopwatch.StartNew();
                        float prob = Predict(proc);
                        swP.Stop(); perfPredictMs += swP.ElapsedMilliseconds;

                        var swE = Stopwatch.StartNew();
                        long freed = EvictProcess(proc, prob, storeToColdCache: prob < coldThreshold);
                        swE.Stop(); perfEvictMs += swE.ElapsedMilliseconds;

                        if (freed >= 0)
                        {
                            perfEvicted++;
                            Interlocked.Increment(ref coldEvicted);
                            Interlocked.Add(ref mbSaved, freed);
                            if (_tournamentModeActive)
                            {
                                tournamentMbThisCycle += freed;
                                _stutterWsFirstTrigger = DateTime.MinValue; // éviction réelle → timer anti-boucle réinitialisé
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

            // Log PERF-TICK → events.log via WriteMarker, uniquement Tournoi ou AntiSwap
            if (_tournamentModeActive || AntiSwapActive)
            {
                string perfMode   = _tournamentModeActive ? "Tournoi" : "AntiSwap-Gaming";
                long   perfOtherMs = swGaming.ElapsedMilliseconds - perfEnumMs - perfPredictMs - perfEvictMs;
                _events.WriteMarker(
                    $"PERF-TICK mode={perfMode} enum={perfEnumMs}ms predict={perfPredictMs}ms" +
                    $" evict={perfEvictMs}ms other={perfOtherMs}ms total={swGaming.ElapsedMilliseconds}ms" +
                    $" | seen={perfSeen} evicted={perfEvicted} skippedCooldown={_tickSkippedCooldown}" +
                    $" rawProcs={rawOtherCount} skip={skipReason}");
            }
        }
        else
        {
            // ── Modes Normal / Turbo : limiter par _maxProcsPerCycle (sauf Turbo) ──
            Process[] procsToProcess;
            if (!turboMode && _maxProcsPerCycle < allProcs.Length)
            {
                procsToProcess = allProcs.Take(_maxProcsPerCycle).ToArray();
                foreach (var dp in allProcs.Skip(_maxProcsPerCycle))
                    try { dp.Dispose(); } catch { }
            }
            else
            {
                procsToProcess = allProcs;
            }

            // ── Instrumentation PERF-TICK (AntiSwap / Normal / Turbo) ──────────
            long perfNormalPredictMs = 0L, perfNormalEvictMs = 0L;
            int  perfNormalSeen = 0, perfNormalEvicted = 0, perfWsFiltered = 0; // wsFiltered = Option B skip count

            // ── Étape A : filtre séquentiel (système + cooldown) ──────────────
            // Coût : un lookup de dictionnaire par PID — négligeable, inutile
            // de paralléliser. Produit toEvict : candidats réels à évincer.
            // En AntiSwap/HighRam avec cooldown actif, toEvict sera typiquement
            // vide ou très réduit → évite de payer l'overhead du ThreadPool pour rien.
            var  swFilter    = Stopwatch.StartNew();
            var  toEvict     = new List<Process>(procsToProcess.Length);

            if (turboMode || _emergencyBypassTicksRemaining > 0)
            {
                // Turbo ou emergency bypass : aucun filtre cooldown — évincer tout sans exception
                toEvict.AddRange(procsToProcess);
            }
            else
            {
                long   nowMsFilter = Environment.TickCount64;
                double cooldownMs  = EvictionCooldownSeconds * 1_000;
                foreach (var p in procsToProcess)
                {
                    try
                    {
                        if (SystemProcesses.Contains(p.ProcessName)) { p.Dispose(); continue; }
                        perfNormalSeen++;

                        // Vérification cooldown : même logique que dans EvictProcess,
                        // déportée ici pour éviter la création de threads sur des candidats
                        // qu'on sait déjà en cooldown (cas dominant en AntiSwap soutenu).
                        long startTicks = 0L;
                        try { startTicks = p.StartTime.Ticks; } catch { }
                        if (_evictionCooldown.TryGetValue(p.Id, out var cd))
                        {
                            bool samePid = startTicks == 0L || cd.StartTimeTicks == 0L
                                        || cd.StartTimeTicks == startTicks;
                            if (samePid && (nowMsFilter - cd.LastEvictedMs) < cooldownMs)
                            {
                                _tickSkippedCooldown++;
                                p.Dispose();
                                continue;
                            }
                        }
                        toEvict.Add(p);
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }

            swFilter.Stop();
            long perfFilterMs = swFilter.ElapsedMilliseconds;
            int  toEvictCount = toEvict.Count;

            // ── Étape B : pré-calcul des probabilités ML par lot (batch) ─────
            // Option A : model.Transform(IDataView) amortit le coût de schéma/pipeline
            //   sur tout le batch au lieu de payer ~5-7ms overhead par appel PredictionEngine.
            // Option B (pré-filtre WS) : processus < MinWorkingSetMbForPredict sont
            //   traités comme prob=0 directement — pas de valeur à classifier.
            // Turbo : pas de ML → court-circuit.
            // Les autres chemins (Gaming, Prédictif) gardent _engine.Predict() individuel.
            const int ParallelEvictThreshold = 20;
            int       parallelDop            = Math.Max(2, Environment.ProcessorCount / 2);

            var swPredictPhase  = Stopwatch.StartNew();
            var toEvictWithProb = new List<(Process Proc, float Prob)>(toEvict.Count);

            if (turboMode || _mlModel is null || _mlCtx is null)
            {
                // Turbo ou sans modèle : prob=0 pour tous (cold path systématique)
                foreach (var p in toEvict) toEvictWithProb.Add((p, 0f));
            }
            else
            {
                // ── Étape B1 : collecte des features + pré-filtre WS ─────────
                var batchProcs  = new List<Process>(toEvict.Count);
                var batchInputs = new List<MlInputRow>(toEvict.Count);

                foreach (var p in toEvict)
                {
                    try
                    {
                        p.Refresh();
                        float wsMB = (float)(p.WorkingSet64 / (1024.0 * 1024.0));

                        // Pré-filtre (Option B) : WS trop faible → éviction directe sans ML
                        if (wsMB < MinWorkingSetMbForPredict)
                        {
                            toEvictWithProb.Add((p, 0f)); // prob=0, évincé comme cold sans ML
                            perfWsFiltered++;
                            continue;
                        }

                        float privMB = (float)(p.PrivateMemorySize64 / (1024.0 * 1024.0));
                        float pfKB   = GetPageFaultDeltaKB(p);
                        float ratio  = privMB > 0f ? wsMB / privMB : 1f;
                        float logWS  = wsMB  > 0f ? (float)Math.Log2(wsMB) : 0f;

                        batchProcs.Add(p);
                        batchInputs.Add(new MlInputRow
                        {
                            WorkingSetMB     = wsMB,
                            PrivateBytesMB   = privMB,
                            PageFaultDeltaKB = Math.Min(pfKB, 100f * 1024f),
                            WsToPrivateRatio = ratio,
                            LogWorkingSet    = logWS,
                        });
                    }
                    catch { try { p.Dispose(); } catch { } }
                }

                // ── Étape B2 : inférence par lot (Option A) ───────────────────
                // model.Transform() valide le schéma une fois et exécute FastTree
                // en mode batch → gain 4-8x vs N appels PredictionEngine individuels.
                if (batchProcs.Count > 0)
                {
                    try
                    {
                        var dataView  = _mlCtx.Data.LoadFromEnumerable(batchInputs);
                        var predicted = _mlCtx.Data.CreateEnumerable<MlOutputRow>(
                            _mlModel.Transform(dataView), reuseRowObject: false).ToList();
                        for (int bi = 0; bi < batchProcs.Count; bi++)
                            toEvictWithProb.Add((batchProcs[bi], predicted[bi].Probability));
                    }
                    catch (Exception ex)
                    {
                        // Fallback : inférence individuelle si le batch échoue (modèle incompatible, etc.)
                        _log.LogWarning("[ML-BATCH] Fallback individuel : {M}", ex.Message);
                        foreach (var p in batchProcs)
                        {
                            float prob = 0f;
                            try { lock (_predictLock) prob = _engine!.Predict(
                                batchInputs[batchProcs.IndexOf(p)]).Probability; } catch { }
                            toEvictWithProb.Add((p, prob));
                        }
                    }
                }
            }

            swPredictPhase.Stop();
            perfNormalPredictMs = swPredictPhase.ElapsedMilliseconds;

            // ── Étape C : éviction — séquentielle si peu de candidats, parallèle sinon ──
            // Seuil empirique : en dessous de ParallelEvictThreshold items, l'overhead
            // du ThreadPool (.NET) dépasse le gain de parallélisme sur des appels P/Invoke
            // courts (OpenProcess + EmptyWorkingSet). Ajustable si profil CPU change.

            // Collecte des appels lents pour diagnostic (Bug 1 — pic isolé evict).
            // Seuil : >300ms par appel individuel = anomalie (AV, GC, pagefile, driver).
            // Aucun overhead en temps normal : le bag n'est lu qu'après le cycle.
            const long SlowEvictCallMs  = 300L;  // par appel individuel
            const long SlowEvictCycleMs = 1500L; // seuil de déclenchement du dump
            var slowEvictions = new System.Collections.Concurrent.ConcurrentBag<string>();

            var swParallel = Stopwatch.StartNew();

            if (turboMode || toEvictWithProb.Count >= ParallelEvictThreshold)
            {
                int dop = turboMode ? Environment.ProcessorCount : parallelDop;
                Parallel.ForEach(toEvictWithProb, new ParallelOptions { MaxDegreeOfParallelism = dop }, item =>
                {
                    using (item.Proc)
                    {
                        try
                        {
                            if (turboMode)
                            {
                                long freed = EvictTurbo(item.Proc);
                                if (freed >= 0) { Interlocked.Increment(ref coldEvicted); Interlocked.Add(ref mbSaved, freed); }
                                return;
                            }
                            if (item.Prob > HotThreshold)
                            {
                                Interlocked.Add(ref faultsAvoided, PrefetchHot(item.Proc));
                                Interlocked.Increment(ref hotPrefetch);
                            }
                            else
                            {
                                var swNE = Stopwatch.StartNew();
                                long freed = EvictProcess(item.Proc, item.Prob, storeToColdCache: item.Prob < coldThreshold);
                                swNE.Stop();
                                long callMs = swNE.ElapsedMilliseconds;
                                if (callMs > SlowEvictCallMs)
                                    slowEvictions.Add($"PID={item.Proc.Id}[{item.Proc.ProcessName}]={callMs}ms");
                                Interlocked.Add(ref perfNormalEvictMs, callMs);
                                if (freed >= 0) { Interlocked.Increment(ref perfNormalEvicted); Interlocked.Increment(ref coldEvicted); Interlocked.Add(ref mbSaved, freed); }
                            }
                        }
                        catch { /* processus terminé ou accès refusé */ }
                    }
                });
            }
            else
            {
                // Séquentiel : toEvictWithProb.Count < ParallelEvictThreshold
                foreach (var (proc, prob) in toEvictWithProb)
                {
                    using (proc)
                    {
                        try
                        {
                            if (prob > HotThreshold)
                            {
                                faultsAvoided += PrefetchHot(proc);
                                hotPrefetch++;
                            }
                            else
                            {
                                var swNE = Stopwatch.StartNew();
                                long freed = EvictProcess(proc, prob, storeToColdCache: prob < coldThreshold);
                                swNE.Stop();
                                long callMs = swNE.ElapsedMilliseconds;
                                if (callMs > SlowEvictCallMs)
                                    slowEvictions.Add($"PID={proc.Id}[{proc.ProcessName}]={callMs}ms");
                                perfNormalEvictMs += callMs;
                                if (freed >= 0) { perfNormalEvicted++; coldEvicted++; mbSaved += freed; }
                            }
                        }
                        catch { /* processus terminé ou accès refusé */ }
                    }
                }
            }

            swParallel.Stop();

            // Dump diagnostic si cycle lent OU au moins un appel individuel anormal.
            // Permet de distinguer : un processus bloquant (AV/driver) vs tous lents
            // (pression pagefile/GC) vs aucun individuellement lent (ThreadPool overhead).
            if (AntiSwapActive && (slowEvictions.Count > 0 || swParallel.ElapsedMilliseconds > SlowEvictCycleMs))
            {
                string slowList = slowEvictions.Count > 0
                    ? string.Join(" | ", slowEvictions)
                    : "none-individually-slow (GC/scheduler/pagefile probable)";
                _events.WriteMarker(
                    $"EVICT-SLOW cycle={swParallel.ElapsedMilliseconds}ms" +
                    $" slowCalls={slowEvictions.Count}/{toEvictWithProb.Count}" +
                    $" : {slowList}");
            }

            // Log PERF-TICK → events.log via WriteMarker, uniquement AntiSwap (pas Turbo ni Repos)
            if (AntiSwapActive)
            {
                _events.WriteMarker(
                    $"PERF-TICK mode=AntiSwap enum={perfEnumMs}ms filter={perfFilterMs}ms" +
                    $" predictPhase={perfNormalPredictMs}ms evict={perfNormalEvictMs}ms evictLoop={swParallel.ElapsedMilliseconds}ms" +
                    $" | seen={perfNormalSeen} toEvict={toEvictCount} evicted={perfNormalEvicted}" +
                    $" skippedCooldown={_tickSkippedCooldown} wsFiltered={perfWsFiltered} total={procsToProcess.Length}procs");
            }
        }

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
            PhysicalMbFreed        = mbSaved,
            IsEcoMode              = _ecoMode,
            IsTournamentMode       = _tournamentModeActive,
            VramMb                 = _vramMb,
            SwapPagesPerSec        = SwapPagesPerSec,
            AntiSwapIntervention   = AntiSwapActive,
            IntervalMs             = _intervalMs,
        });

        _log.LogDebug(
            "Tick {L}ms | cold={C} hot={H} saved≈{M}MB gaming={G} swap={S} cpu={P:F0}% ram={R:P0} turbo={U}",
            sw.ElapsedMilliseconds, coldEvicted, hotPrefetch, mbSaved,
            _gamingModeActive, AntiSwapActive, _cpuPct, _ramUsedPct, turboMode);

        // ── Comparaison signal mémoire : ramUsedPct (courant) vs vraiePressionPct (observé) ──
        // Observation seule — ne modifie pas la logique de paliers.
        // standbyMb = cache disque récupérable en <1ms → ne représente pas de vraie pression.
        // vraiePressionPct = (Total - Available - Standby) / Total = pages réellement committées.
        if (_tickCount % 10 == 0)
        {
            _log.LogInformation(
                "[MEM-SIGNAL] avail={A}Mo (seuil<{E} >sortie{X}) | highRam={H} | ramUsedPct={U:P1} | standby={S}Mo | total={T}Mo",
                tickAvailMb, _highRamThresholdMb, _highRamExitMb, _highRamActive,
                _ramUsedPct, _standbyMb, tickTotalMb);
        }
    }

    // ── Détection gaming — 3 niveaux ─────────────────────────────────────────
    //
    //   Niveau 1 : blacklist launchers   → O(1), retour immédiat si launcher au premier plan
    //   Niveau 2 : whitelist par nom     → KnownGameProcesses + fallback >1 Go
    //   Niveau 3 : heuristique           → plein écran + GPU Engine 3D + hystérèse 12 s
    //
    // Rendu non-static pour accéder à _log et _tickCount pour les diagnostics.
    private (bool IsGaming, string GameName) DetectGaming(Process[] procs)
    {
        bool logThisTick  = (++_tickCount % 10 == 1); // logguer 1 tick sur 10 (~8-30 s)
        // [DIAG-TEMP] cadence de diagnostic : toutes les 3 ticks (~6 s) hors Gaming,
        // pour rendre visible dans events.log ce qui se passe en L3 pendant les tests.
        // À retirer une fois l'heuristique validée sur terrain.
        bool diagThisTick = !_gamingModeActive && (_tickCount % 3 == 0);

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

        // ── Résolution fenêtre au premier plan via foreground.json (pont Phase4→Phase3) ──
        // Phase3 (Session 0) ne peut pas appeler GetForegroundWindow() directement.
        // Phase4 (session interactive) écrit foreground.json toutes les 500 ms.
        var (fgPid, fgName, fgFullscreen, fgWinW, fgWinH, fgScrW, fgScrH) = ReadForegroundInfo();
        // Mettre à jour _foregroundPid ici (une seule lecture par tick) — utilisé
        // par ShouldEvict() pour ne jamais évincer le process au premier plan.
        _foregroundPid = fgPid;

        // ── Niveau 1 : blacklist launchers — O(1), coût quasi nul ────────────────
        // Si le launcher est au premier plan → jamais Gaming, heuristique réinitialisée.
        if (!string.IsNullOrEmpty(fgName) && LauncherBlacklist.Contains(fgName))
        {
            ResetHeuristic();
            if (logThisTick)
            {
                _log.LogInformation("[GAMING-L1] Launcher '{N}' au premier plan → Gaming bloqué (blacklist)", fgName);
                _events.WriteMarker($"GAMING-DETECT level=blacklist fg={fgName}");
            }
            return (false, string.Empty);
        }

        // ── Niveau 2a : liste blanche par nom (KnownGameProcesses) ───────────────
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
                    _log.LogInformation("[GAMING-L2] MATCH whitelist : {G} (pid={P})", name, p.Id);
                    Console.WriteLine($"[RAM-AI] MATCH GAMING : {name}  (pid={p.Id})");
                    return (true, name);
                }
            }
            catch { /* processus disparu ou accès refusé */ }
        }

        // ── Niveau 2b : fallback >1 Go (non blacklisté, seulement pour DÉCLENCHER) ─
        // CRITIQUE : si _gamingModeActive est déjà vrai (ex : un jeu vient d'être fermé),
        // ce bloc est ignoré pour permettre la désactivation du mode gaming.
        // LauncherBlacklist protège ici aussi : Riot Client, Steam, etc. > 1 Go ignorés.
        if (!_gamingModeActive && !_tournamentModeActive && flagContent == "auto")
        {
            foreach (var p in procs)
            {
                try
                {
                    string name = p.ProcessName;
                    if (p.WorkingSet64 > 1L * 1024 * 1024 * 1024
                        && !SystemProcesses.Contains(name)
                        && !LauncherBlacklist.Contains(name)
                        && !name.Equals("RamAI.Phase3", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("RamAI.Phase4", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogInformation("[GAMING-L2] MATCH >1Go : {G} (pid={P})", name, p.Id);
                        Console.WriteLine($"[RAM-AI] MATCH GAMING (>1Go RAM) : {name} (pid={p.Id})");
                        return (true, $"{name} (>1Go RAM)");
                    }
                }
                catch { }
            }
        }

        // ── Niveau 3 : heuristique — plein écran + GPU Engine 3D + hystérèse ─────
        bool heuristic = CheckHeuristic(fgPid, fgName, fgFullscreen, fgWinW, fgWinH, fgScrW, fgScrH, logThisTick, diagThisTick);
        if (heuristic)
            return (true, $"{fgName} (heuristique)");

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

    // ── Heuristique Niveau 3 : plein écran + GPU Engine 3D + hystérèse ────────

    private bool CheckHeuristic(int fgPid, string fgName,
                                bool isFullscreen, int winW, int winH, int scrW, int scrH,
                                bool logThisTick, bool diagThisTick)
    {
        if (fgPid <= 0 || string.IsNullOrEmpty(fgName))
        {
            // Phase4 fermé ou fichier foreground.json absent/périmé — pas de données de premier plan
            if (diagThisTick)
                _events.WriteMarker($"DIAG-HEURISTIC fg=(none) pid={fgPid} — foreground.json absent/périmé (Phase4 fermé ?)");
            ApplyHeuristicSignalLoss();
            return _heuristicActive;
        }

        RefreshHeuristicGpuIfNeeded(fgPid);
        float gpu3D        = SampleHeuristicGpu3D(out int gpuErrors);
        bool  hasGpu3D     = gpu3D >= HeuristicGpu3DThresholdPct;
        bool  signals      = isFullscreen && hasGpu3D;
        int   ctrCount     = _gpuEngine3DCounters.Count;

        double hysterSec = _heuristicSignalSince == DateTime.MinValue ? 0.0
                         : (DateTime.UtcNow - _heuristicSignalSince).TotalSeconds;

        // [DIAG-TEMP] Marker events.log toutes les ~6 s — couvre les 4 hypothèses :
        //   H1 multi-écran  : winW/H vs scrW/H (ex. 2560x1440 vs 3840x1080 → fs=false)
        //   H2 mauvais PID  : fg= montre le vrai process au premier plan
        //   H3 anti-cheat   : ctr=0 err=N avail=true → catégorie présente mais PID bloqué
        //   H4 nommage inst : ctr=0 err=0 avail=true → aucune instance engtype_3D trouvée
        if (diagThisTick || logThisTick)
        {
            _events.WriteMarker(
                $"DIAG-HEURISTIC fg={fgName} pid={fgPid}" +
                $" | fs={isFullscreen}[win={winW}x{winH} scr={scrW}x{scrH}]" +
                $" | gpu3D={gpu3D:F1}%(ctr={ctrCount} err={gpuErrors} avail={_gpuEngineAvailable})" +
                $" | signals={signals} hyster={hysterSec:F1}s/{HeuristicEnterSeconds}s active={_heuristicActive}");
        }

        if (logThisTick)
            _log.LogInformation(
                "[GAMING-L3] Heuristique fg={N} fullscreen={F} gpu3D={G:F1}% signals={S} active={A}",
                fgName, isFullscreen, gpu3D, signals, _heuristicActive);

        var now = DateTime.UtcNow;

        if (signals)
        {
            _heuristicLostSince = DateTime.MinValue;
            if (_heuristicSignalSince == DateTime.MinValue)
                _heuristicSignalSince = now;

            if (!_heuristicActive &&
                (now - _heuristicSignalSince).TotalSeconds >= HeuristicEnterSeconds)
            {
                _heuristicActive = true;
                _log.LogInformation(
                    "[GAMING-L3] Gaming heuristique ON — {N} (plein écran + GPU 3D {G:F1}% continu ≥{S}s)",
                    fgName, gpu3D, HeuristicEnterSeconds);
                Console.WriteLine(
                    $"[RAM-AI] MATCH GAMING (heuristique) : {fgName} — plein écran + GPU 3D {gpu3D:F1}%");
                _events.WriteMarker(
                    $"GAMING-DETECT level=heuristic proc={fgName} gpu3D={gpu3D:F1}% → Gaming ON");
            }
        }
        else
        {
            ApplyHeuristicSignalLoss();
        }

        return _heuristicActive;
    }

    private void ApplyHeuristicSignalLoss()
    {
        _heuristicSignalSince = DateTime.MinValue;
        if (!_heuristicActive) return;

        var now = DateTime.UtcNow;
        if (_heuristicLostSince == DateTime.MinValue)
            _heuristicLostSince = now;

        if ((now - _heuristicLostSince).TotalSeconds >= HeuristicExitSeconds)
        {
            _heuristicActive    = false;
            _heuristicLostSince = DateTime.MinValue;
            _log.LogInformation(
                "[GAMING-L3] Gaming heuristique OFF — signaux absents depuis ≥{S}s", HeuristicExitSeconds);
            _events.WriteMarker(
                $"GAMING-DETECT level=heuristic → Gaming OFF (signaux perdus {HeuristicExitSeconds}s)");
        }
    }

    private void ResetHeuristic()
    {
        _heuristicSignalSince = DateTime.MinValue;
        _heuristicLostSince   = DateTime.MinValue;
        _heuristicActive      = false;
    }

    // Rafraîchit les compteurs GPU Engine 3D seulement si le PID au premier plan a changé.
    // Coût : GetInstanceNames + création PerformanceCounter — fait une seule fois par PID.
    // En jeu, le PID au premier plan reste constant pendant des minutes → overhead quasi nul.
    private void RefreshHeuristicGpuIfNeeded(int pid)
    {
        if (!_gpuEngineAvailable || pid == _heuristicFgPid) return;
        _heuristicFgPid = pid;

        foreach (var c in _gpuEngine3DCounters) try { c.Dispose(); } catch { }
        _gpuEngine3DCounters.Clear();

        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _gpuEngineAvailable = false;
                // [DIAG-TEMP] WriteMarker pour rendre visible dans events.log (LogDebug était invisible)
                string absent = "[GAMING-L3] GPU Engine : catégorie 'GPU Engine' absente — heuristique GPU désactivée";
                _log.LogInformation(absent);
                _events.WriteMarker(absent);
                return;
            }
            var    cat      = new PerformanceCounterCategory("GPU Engine");
            var    allInsts = cat.GetInstanceNames();
            string pidTag   = $"pid_{pid}_";
            int    matched  = 0;

            foreach (var inst in allInsts)
            {
                if (inst.Contains(pidTag,       StringComparison.OrdinalIgnoreCase) &&
                    inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        pc.NextValue(); // warm-up — premier appel toujours 0 sur les compteurs de taux
                        _gpuEngine3DCounters.Add(pc);
                        matched++;
                    }
                    catch (Exception ex)
                    {
                        // [DIAG-TEMP] Exception sur un compteur individuel — anti-cheat / accès refusé ?
                        string errMsg = $"[GAMING-L3] GPU Engine : exception création compteur inst={inst} — {ex.Message}";
                        _log.LogInformation(errMsg);
                        _events.WriteMarker(errMsg);
                    }
                }
            }

            // [DIAG-TEMP] Log systématique — une fois par PID — pour confirmer l'état dans events.log
            string initMsg = matched > 0
                ? $"[GAMING-L3] GPU Engine PID {pid} : {matched} compteur(s) engtype_3D trouvé(s) (total instances={allInsts.Length})"
                : $"[GAMING-L3] GPU Engine PID {pid} : 0 instance engtype_3D — pidTag='{pidTag}' total={allInsts.Length} instances";
            _log.LogInformation(initMsg);
            _events.WriteMarker(initMsg);
        }
        catch (Exception ex)
        {
            // [DIAG-TEMP] Exception globale — catégorie présente mais inaccessible (droits, driver ?)
            string msg = $"[GAMING-L3] GPU Engine exception globale : {ex.Message}";
            _log.LogInformation(msg);
            _events.WriteMarker(msg);
            _gpuEngineAvailable = false;
        }
    }

    // [DIAG-TEMP] Surcharge avec out errors pour détecter H3 (exceptions silencieuses anti-cheat).
    private float SampleHeuristicGpu3D(out int errors)
    {
        errors = 0;
        if (_gpuEngine3DCounters.Count == 0) return 0f;
        float total = 0f;
        foreach (var c in _gpuEngine3DCounters)
        {
            try { total += c.NextValue(); }
            catch { errors++; }
        }
        return total;
    }

    private float SampleHeuristicGpu3D() => SampleHeuristicGpu3D(out _);

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

    private static bool IsGameProcess(string name) => KnownGameProcesses.Contains(name);

    // ── Ultra : Optimisation prédictive ──────────────────────────────────────

    /// <summary>
    /// Maintient un historique des Mo de RAM disponibles.
    // ── Détection précoce chute availMb (observation seule, log [PRESSWAP-DBG]) ──

    private void CheckPreSwapDrop(long currentAvailMb)
    {
        // ── Détection urgence single-tick (Tournoi / AntiSwap seulement) ─────
        // Une chute > EmergencyBypassRamDropMb en un seul tick (500ms) est le signal
        // avant-coureur du pic de swap (observé 01/07 : 1568 Mo en un tick, swap explosé
        // 12s plus tard à 283 811 p/s alors que 103/122 candidats étaient en cooldown).
        // Conditions de réarmement : bypass inactif + délai 30s post-dernier-bypass.
        if ((_tournamentModeActive || AntiSwapActive)
            && _emergencyBypassTicksRemaining == 0
            && _availMbRingIdx >= 2
            && (Environment.TickCount64 - _emergencyBypassLastEndMs) > EmergencyBypassRearmDelayMs)
        {
            int ringLen = _availMbRing.Length;
            long prev   = _availMbRing[(_availMbRingIdx - 2) % ringLen]; // tick i-1
            long curr   = _availMbRing[(_availMbRingIdx - 1) % ringLen]; // tick i (courant)
            long drop = prev - curr;                              // positif = chute

            if (drop > EmergencyBypassRamDropMb)
            {
                _emergencyBypassTicksRemaining = EmergencyBypassTicks;
                _emergencyBypassEvictedCount   = 0;
                _emergencyBypassStartMs        = Environment.TickCount64;
                _events.WriteMarker(
                    $"[EMERGENCY-BYPASS] trigger=RamDrop delta={drop}Mo bypassTicks={EmergencyBypassTicks}");
            }
        }

        // Pas assez de ticks pour calculer 3 deltas consécutifs
        if (_availMbRingIdx < PreSwapConsecTicks + 1) return;
        // Cooldown actif : inhiber pendant K ticks après HighRam/AntiSwap
        if (_preSwapCooldownRemaining > 0) return;

        // Lire les 4 dernières valeurs dans l'ordre chronologique
        int n = _availMbRing.Length; // 4
        long v0 = _availMbRing[(_availMbRingIdx - 4) % n]; // tick i-3
        long v1 = _availMbRing[(_availMbRingIdx - 3) % n]; // tick i-2
        long v2 = _availMbRing[(_availMbRingIdx - 2) % n]; // tick i-1
        long v3 = _availMbRing[(_availMbRingIdx - 1) % n]; // tick i (courant)

        long d1 = v0 - v1; // positif = chute
        long d2 = v1 - v2;
        long d3 = v2 - v3;

        bool allDropping = d1 > PreSwapDropThresholdMb
                        && d2 > PreSwapDropThresholdMb
                        && d3 > PreSwapDropThresholdMb;

        if (!allDropping) return;

        long totalDrop = v0 - v3;
        _log.LogInformation(
            "[PRESSWAP-DBG] Chute rapide : {D0}/{D1}/{D2} Mo/tick (3 ticks consécutifs > {S}Mo) | " +
            "total={T}Mo | avail={A}Mo | highRam={H} | antiSwap={AS}",
            d1, d2, d3, PreSwapDropThresholdMb, totalDrop, currentAvailMb, _highRamActive, AntiSwapActive);
    }

    // ── Trim prédictif (pente availMb → estimation temps avant HighRam) ───────

    /// <summary>
    /// Calcule la pente de décroissance d'availMb sur les derniers ticks.
    /// Si la projection indique un franchissement du seuil HighRam dans les
    /// PredictiveTrimLeadTimeSec secondes, déclenche une éviction anticipée
    /// et loggue [PREDICTIVE_TRIM] pour analyse post-session.
    /// N'agit pas si HighRam ou AntiSwap est déjà actif, ni en mode Gaming/Tournoi/Éco.
    /// </summary>
    private void CheckPredictiveTrim(long currentAvailMb, Process[] allProcs,
        ref int coldEvicted, ref long mbSaved)
    {
        // Alimenter le ring buffer (utilise _tickAvailMb déjà mis en cache — zéro appel API supplémentaire)
        _predictRing[_predictRingIdx % PredictiveTrimRingSize] = currentAvailMb;
        _predictRingIdx++;
        if (_predictSampleCount < PredictiveTrimRingSize) _predictSampleCount++;

        // Inhibition par palier :
        //   HighRam    → déjà en pression, anticipation inutile (reset cooldown pour relancer dès sortie)
        //   AntiSwap   → priorité absolue déjà ultra-réactif (500ms), pas besoin de superposer
        //   Tournoi    → 500ms déjà agressif, le moindre trim supplémentaire risque le stutter
        //   Éco        → contexte batterie, on économise les cycles CPU
        //   Gaming     → AUTORISÉ : c'est précisément le scénario cible (pression GPU pendant le jeu)
        if (_highRamActive || AntiSwapActive) { _predictiveTrimCooldown = 0; return; }
        if (_tournamentModeActive || _ecoMode) return;
        if (_predictiveTrimCooldown > 0) { _predictiveTrimCooldown--; return; }
        if (_predictSampleCount < PredictiveTrimRingSize) return;
        if (_highRamThresholdMb == 0) return; // seuils pas encore initialisés

        // Régression : pente moyenne (Mo/tick) sur les N derniers échantillons
        // Lire dans l'ordre chronologique depuis le ring circulaire
        int   n         = PredictiveTrimRingSize;
        long  oldest    = _predictRing[(_predictRingIdx - n)     % n];
        long  newest    = _predictRing[(_predictRingIdx - 1)     % n];
        double dropPerTick = (double)(oldest - newest) / (n - 1); // positif = avail décroissante

        if (dropPerTick <= 0) return; // avail stable ou en hausse — rien à faire

        // Temps estimé avant franchissement du seuil d'entrée HighRam
        double marginMb = currentAvailMb - _highRamThresholdMb;
        if (marginMb <= 0) return; // déjà en dessous du seuil (HighRam devrait être actif)

        double ticksToThreshold  = marginMb / dropPerTick;
        double secsToThreshold   = ticksToThreshold * _intervalMs / 1000.0;

        if (secsToThreshold > PredictiveTrimLeadTimeSec) return; // pas encore urgent

        _log.LogInformation(
            "[PREDICTIVE_TRIM] Seuil HighRam dans ~{S:F1}s (pente={D:F0}Mo/tick, avail={A}Mo, seuil={E}Mo) → éviction anticipée",
            secsToThreshold, dropPerTick, currentAvailMb, _highRamThresholdMb);
        Console.WriteLine(
            $"[RAM-AI] 🔮 PREDICTIVE_TRIM : HighRam dans ~{secsToThreshold:F1}s — éviction anticipée");
        _events.WriteMarker(
            $"PREDICTIVE_TRIM — seuil dans ~{secsToThreshold:F1}s | pente={dropPerTick:F0}Mo/tick | avail={currentAvailMb}Mo");

        // Éviction anticipée : même logique que le cold path HighRam
        int trimmed = 0;
        foreach (var p in allProcs)
        {
            try
            {
                if (SystemProcesses.Contains(p.ProcessName)) continue;
                float prob = Predict(p);
                if (prob > HotThreshold) continue;

                long freed = EvictProcess(p, prob, storeToColdCache: prob < ColdThreshold);
                if (freed >= 0) { coldEvicted++; mbSaved += freed; trimmed++; }
            }
            catch { }
        }

        _log.LogInformation("[PREDICTIVE_TRIM] {N} processus traités, {M}Mo libérés", trimmed, mbSaved);
        _predictiveTrimCooldown = PredictiveTrimCooldownTicks;
    }

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

    // ── Détection et application du mode Éco (batterie) ─────────────────────

    private void RefreshEcoMode()
    {
        bool onBattery = NativeMemory.IsOnBattery() || File.Exists(EcoFlagPath);

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
                _intervalMs, _maxProcsPerCycle, _batchSleepMs);
            Console.WriteLine($"[RAM-AI] 🔋 MODE ÉCO ON (intervalle={_intervalMs}ms, max {_maxProcsPerCycle} procs/cycle)");
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
        // ── Protection : premier plan ──────────────────────────────────────────
        if (proc.Id == _foregroundPid) return -1L;

        // ── Cooldown par PID : ne pas réévincer un processus trimé récemment ──
        // Capture StartTime pour distinguer un vrai même processus d'un PID recyclé.
        long nowMs = Environment.TickCount64;
        long startTimeTicks = 0L;
        try { startTimeTicks = proc.StartTime.Ticks; } catch { /* accès refusé sur certains procs système */ }

        if (_emergencyBypassTicksRemaining == 0   // bypass inactif → vérifier le cooldown normal
            && _evictionCooldown.TryGetValue(proc.Id, out var cd))
        {
            bool samePid = startTimeTicks == 0L || cd.StartTimeTicks == 0L || cd.StartTimeTicks == startTimeTicks;
            if (samePid && (nowMs - cd.LastEvictedMs) < EvictionCooldownSeconds * 1_000)
            {
                Interlocked.Increment(ref _tickSkippedCooldown);
                return -1L;
            }
        }

        // ── Protection : WS modifié dans les 10 dernières secondes ────────────
        long wsMbNow = proc.WorkingSet64 / (1024L * 1024L);
        if (_wsTracker.TryGetValue(proc.Id, out var wsEntry)
            && (nowMs - wsEntry.TickMs) < 10_000
            && Math.Abs(wsMbNow - wsEntry.WsMb) > 5)
        {
            return -1L;
        }
        _wsTracker[proc.Id] = (wsMbNow, nowMs);

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

            // EmptyWorkingSet déplace les pages vers la liste standby (soft trim).
            // SetProcessWorkingSetSizeEx(-1,-1) force un hard trim immédiat → évite les page faults
            // en Mode Tournoi où chaque interruption jeu est visible comme stutter.
            bool ok = NativeMemory.EmptyWorkingSet(hProc);
            if (!_tournamentModeActive)
                NativeMemory.SetProcessWorkingSetSizeEx(hProc, new IntPtr(-1), new IntPtr(-1), 0);

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

            // Enregistrer l'éviction pour le cooldown (empêche une rééviction immédiate)
            _evictionCooldown[proc.Id] = (Environment.TickCount64, startTimeTicks);

            // Comptabiliser pour le log [EMERGENCY-BYPASS-EXPIRED]
            if (_emergencyBypassTicksRemaining > 0)
                Interlocked.Increment(ref _emergencyBypassEvictedCount);

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

    private void PurgeWsTracker()
    {
        long cutoffMs = Environment.TickCount64 - 60_000; // 60s
        foreach (var pid in _wsTracker.Keys.ToArray())
        {
            if (_wsTracker.TryGetValue(pid, out var entry) && entry.TickMs < cutoffMs)
                _wsTracker.TryRemove(pid, out _);
        }
    }

    private void PurgeEvictionCooldown()
    {
        // Retire les entrées dont la dernière éviction remonte à plus de 30s
        // (processus terminé ou cooldown largement expiré — évite la fuite mémoire).
        long cutoffMs = Environment.TickCount64 - 30_000;
        foreach (var pid in _evictionCooldown.Keys.ToArray())
        {
            if (_evictionCooldown.TryGetValue(pid, out var entry) && entry.LastEvictedMs < cutoffMs)
                _evictionCooldown.TryRemove(pid, out _);
        }
    }

    public void Dispose()
    {
        _swapCounter?.Dispose();
        _cpuCounter?.Dispose();
        _standbyReserveCounter?.Dispose();
        _standbyNormalCounter?.Dispose();
        _standbyCoreCounter?.Dispose();
        _engine?.Dispose();   // null si mode sans ML
        _gpuMonitor.Dispose();
        foreach (var c in _gpuEngine3DCounters) try { c.Dispose(); } catch { }
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
