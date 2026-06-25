using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;
using Microsoft.Win32;

namespace RamAI.Phase4.ViewModels;


public sealed partial class MainViewModel : ObservableObject
{
    // ── Mode Bêta testeur ─────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isBetaMode;
    [ObservableProperty] private string _betaExpiryText = string.Empty;

    // ── Métriques principales ──────────────────────────────────────────────────
    [ObservableProperty] private double _currentRamUsedGb;
    [ObservableProperty] private string _improvementText = "—";
    [ObservableProperty] private double _totalRamFreedGb;
    [ObservableProperty] private long   _processesOptimized;

    // ── Métriques secondaires ──────────────────────────────────────────────────
    [ObservableProperty] private long   _totalMbSaved;
    [ObservableProperty] private string _serviceStatus     = "Inconnu";
    [ObservableProperty] private string _licenseTierLabel  = "Aucune";
    [ObservableProperty] private string _licenseCacheLabel = "—";
    [ObservableProperty] private string _lastUpdateText    = "—";

    // ── Mode Gaming ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isGamingModeActive;
    [ObservableProperty] private string _detectedGameName  = string.Empty;
    [ObservableProperty] private bool   _forceGamingMode;
    [ObservableProperty] private string _detectedGameLabel = string.Empty;

    // ── Mode Éco (batterie ou forcé) ─────────────────────────────────────────
    [ObservableProperty] private bool   _isEcoModeActive;
    [ObservableProperty] private string _ecoModeText       = string.Empty;
    [ObservableProperty] private bool   _forceEcoMode;

    // ── Anti-Swap ─────────────────────────────────────────────────────────────
    [ObservableProperty] private float _swapPagesPerSec;
    [ObservableProperty] private bool  _isAntiSwapActive;

    // ── Ultra ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isUltraModeActive;
    [ObservableProperty] private bool   _isTournamentModeActive;
    [ObservableProperty] private string _vramInfoText       = string.Empty;

    // ── RAM au démarrage ─────────────────────────────────────────────────────
    private long _baselineRamUsedMb;
    private long _totalRamMb;

    // ── Stats persistées ──────────────────────────────────────────────────────
    private readonly StatsService   _statsService   = new();
    private readonly MeasureService _measureService = new();
    private readonly StatsData      _stats;
    private readonly DateTime       _sessionStart   = DateTime.Now;

    // ── Mode courant (mis à jour dans OnNewEntry, lu depuis MeasureLoop) ──────
    // Volatile car lu depuis un thread pool, écrit depuis le dispatcher.
    private volatile string _currentActiveMode = "Standard";

    // ── Tracking des transitions de mode (pour compter les activations) ────────
    private bool _prevGaming;
    private bool _prevEco;

    // ── Compteurs de ticks par mode aujourd'hui ───────────────────────────────
    private int _todayGamingTicks;
    private int _todayEcoTicks;

    // ── Pic de RAM libérée en un seul tick (cette session) ────────────────────
    private long _peakTickFreedMb;

    // ── RAM libérée cette session (pour "meilleure session") ─────────────────
    private long _sessionFreedMb;

    // ── Détection de relances de processus ────────────────────────────────────
    // On surveille uniquement les processus en instance unique pour éviter le
    // bruit de svchost / RuntimeBroker qui ont de nombreuses instances.
    private HashSet<string> _prevSingleInstanceProcs = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _goneLastTick             = new(StringComparer.OrdinalIgnoreCase);
    // Processus qui se sont relancés pendant la session : name → nombre de relances
    private readonly Dictionary<string, int> _restartCounts = new(StringComparer.OrdinalIgnoreCase);

    // ── Dossier partagé Phase3 ↔ Phase4 ───────────────────────────────────────
    private static readonly string SharedFlagDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI");

    private static readonly string ForceFlagPath      = Path.Combine(SharedFlagDir, "gaming_mode.force");
    private static readonly string TurboFlagPath      = Path.Combine(SharedFlagDir, "turbo_mode.force");
    private static readonly string TournamentFlagPath = Path.Combine(SharedFlagDir, "tournament_mode.force");
    private static readonly string EcoFlagPath        = Path.Combine(SharedFlagDir, "eco_mode.force");

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly LogWatcherService _logWatcher;
    private readonly LicenseService    _licenseService;

    public MainViewModel(LogWatcherService logWatcher, LicenseService licenseService)
    {
        _logWatcher     = logWatcher;
        _licenseService = licenseService;

        _logWatcher.NewEntry           += OnNewEntry;
        _licenseService.LicenseChanged += OnLicenseChanged;
        OnLicenseChanged(_licenseService.Current);
        IsUltraModeActive = _licenseService.Current.IsUltra;

        // Synchroniser ForceGamingMode depuis le fichier flag au démarrage.
        if (ReadFlagContent() == "manual")
        {
            ForceGamingMode    = true;
            IsGamingModeActive = true;
            DetectedGameName   = "Mode Gaming FORCÉ";
            DetectedGameLabel  = "Mode Gaming FORCÉ";
        }

        // Synchroniser ForceEcoMode depuis le fichier flag au démarrage.
        if (File.Exists(EcoFlagPath))
        {
            ForceEcoMode    = true;
            IsEcoModeActive = true;
            EcoModeText     = "🔋 Mode Éco forcé (manuel)";
        }

        // Créer le dossier partagé si nécessaire
        try { Directory.CreateDirectory(SharedFlagDir); } catch { }

        // Bandeau bêta
        if (licenseService.Current.Tier == Models.LicenseTier.Beta && licenseService.BetaExpiryDate.HasValue)
        {
            IsBetaMode     = true;
            BetaExpiryText = $"🧪 Version testeur — expire le {licenseService.BetaExpiryDate.Value.ToLocalTime():dd/MM/yyyy}";
        }

        var (totalMb, usedMb) = SystemMemory.GetPhysicalMemoryMb();
        _baselineRamUsedMb = usedMb;
        _totalRamMb        = totalMb;
        CurrentRamUsedGb   = usedMb / 1024.0;

        // Charger les stats et réinitialiser les compteurs du jour si nécessaire
        _stats = _statsService.Load();
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        if (_stats.TodayDate != todayKey)
        {
            _stats.TodayDate               = todayKey;
            _stats.TodayRamFreedGb         = 0.0;
            _stats.TodayProcessesOptimized = 0;
        }
        _stats.TotalSessions++;

        var __ = EnsurePhase3RunningAsync();
        var _1 = RefreshLoop();
        var _2 = MeasureLoop();    // Point 1 : mesures toutes les 10s
    }

    // ── Lecture du contenu du flag ────────────────────────────────────────────

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

    // ── Réception des entrées log Phase 3 ─────────────────────────────────────

    private void OnNewEntry(EventEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // ── RAM libérée ──
            if (entry.PhysicalMbFreed >= 1)
            {
                TotalMbSaved     += entry.PhysicalMbFreed;
                TotalRamFreedGb   = TotalMbSaved / 1024.0;
                _sessionFreedMb  += entry.PhysicalMbFreed;

                if (entry.PhysicalMbFreed > _peakTickFreedMb)
                    _peakTickFreedMb = entry.PhysicalMbFreed;

                double freedGb = entry.PhysicalMbFreed / 1024.0;
                _stats.TotalRamFreedGb += freedGb;
                _stats.TodayRamFreedGb += freedGb;

                // Attribuer la RAM libérée au mode actif
                if      (entry.IsGamingMode || ForceGamingMode) _stats.GamingRamFreedGb += freedGb;
                else if (entry.IsEcoMode)                       _stats.EcoRamFreedGb    += freedGb;
            }

            // ── Processus ──
            long procsThisTick = entry.ColdEvicted;
            ProcessesOptimized            += procsThisTick;
            _stats.TotalProcessesOptimized += procsThisTick;
            _stats.TodayProcessesOptimized += procsThisTick;
            LastUpdateText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            // ── Mode Gaming ──
            bool gamingNow = entry.IsGamingMode || ForceGamingMode;
            IsGamingModeActive = gamingNow;
            if (!_prevGaming && gamingNow) _stats.GamingActivations++;
            _prevGaming = gamingNow;
            if (gamingNow) _todayGamingTicks++;

            if (entry.IsGamingMode && !string.IsNullOrEmpty(entry.GameName))
            {
                DetectedGameName  = entry.GameName;
                DetectedGameLabel = $"Jeu détecté : {entry.GameName}";
            }
            else if (!entry.IsGamingMode && !ForceGamingMode)
            {
                DetectedGameName  = string.Empty;
                DetectedGameLabel = string.Empty;
            }
            else if (ForceGamingMode && !entry.IsGamingMode)
            {
                DetectedGameName  = "Mode Gaming FORCÉ";
                DetectedGameLabel = "Mode Gaming FORCÉ";
            }

            // ── Mode Éco ──
            bool ecoNow = entry.IsEcoMode;
            if (ecoNow)
            {
                IsEcoModeActive = true;
                EcoModeText     = "🔋 Mode Éco actif";
            }
            else
            {
                IsEcoModeActive = false;
                EcoModeText     = string.Empty;
            }
            if (!_prevEco && ecoNow) _stats.EcoActivations++;
            _prevEco = ecoNow;
            if (ecoNow) _todayEcoTicks++;

            // ── Anti-Swap ──
            SwapPagesPerSec  = entry.SwapPagesPerSec;
            IsAntiSwapActive = entry.AntiSwapIntervention;

            // ── Ultra ──
            IsTournamentModeActive = entry.IsTournamentMode;
            VramInfoText = entry.VramMb > 0
                ? $"VRAM : {entry.VramMb} Mo"
                : string.Empty;

            // ── Mettre à jour le mode courant (lu par MeasureLoop) ──
            _currentActiveMode =
                entry.IsTournamentMode                  ? "Tournoi" :
                (entry.IsGamingMode || ForceGamingMode) ? "Gaming"  :
                ecoNow                                  ? "Éco"     :
                                                          "Standard";
        });
    }

    private void OnLicenseChanged(LicenseInfo info)
    {
        LicenseTierLabel  = info.TierLabel;
        LicenseCacheLabel = info.CacheLimitLabel;
        IsUltraModeActive = info.IsUltra;
    }

    /// <summary>Active / désactive le mode Tournoi (Ultra uniquement).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleTournamentMode()
    {
        if (!IsUltraModeActive) return;

        IsTournamentModeActive = !IsTournamentModeActive;
        try
        {
            if (IsTournamentModeActive)
            {
                Directory.CreateDirectory(SharedFlagDir);
                File.WriteAllText(TournamentFlagPath, "tournament");
                _stats.TournamentUseCount++;
            }
            else
            {
                if (File.Exists(TournamentFlagPath)) File.Delete(TournamentFlagPath);
            }
        }
        catch { }
    }

    // ── Cache statut service (évite sc.exe toutes les 2s → source de stutter) ────
    private DateTime _lastServiceRefresh  = DateTime.MinValue;
    private string   _cachedServiceStatus = "Inconnu";

    // ── Boucle de rafraîchissement UI ─────────────────────────────────────────
    // Délai normal : 2 s. Délai Tournoi : 5 s (réduit la pression CPU/scheduler).
    // GetServiceStatus() (spawn sc.exe) mis en cache : appel max toutes les 10 s.

    private async Task RefreshLoop()
    {
        while (true)
        {
            bool inTournament = IsTournamentModeActive;

            // Rafraîchir le statut service max toutes les 10 s pour éviter
            // de spawner sc.exe à chaque tick (cause de micro-spikes CPU en jeu).
            if ((DateTime.UtcNow - _lastServiceRefresh).TotalSeconds >= 10)
            {
                _cachedServiceStatus = GetServiceStatus();
                _lastServiceRefresh  = DateTime.UtcNow;
            }
            var    status      = _cachedServiceStatus;
            var    (_, usedMb) = SystemMemory.GetPhysicalMemoryMb();
            string flagContent = ReadFlagContent();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                ServiceStatus    = status;
                CurrentRamUsedGb = usedMb / 1024.0;
                UpdateImprovement(usedMb);

                if (flagContent == "manual" && !ForceGamingMode)
                {
                    ForceGamingMode    = true;
                    IsGamingModeActive = true;
                    DetectedGameName   = "Mode Gaming FORCÉ";
                    DetectedGameLabel  = "Mode Gaming FORCÉ";
                }
                else if (flagContent != "manual" && ForceGamingMode)
                {
                    ForceGamingMode    = false;
                    IsGamingModeActive = false;
                    DetectedGameName   = string.Empty;
                    DetectedGameLabel  = string.Empty;
                }
            });

            // Intervalle réduit en Mode Tournoi pour libérer le scheduler Windows
            await Task.Delay(inTournament ? 5_000 : 2_000);
        }
        // ReSharper disable once FunctionNeverReturns
    }

    // ── Point 1 : Mesures toutes les 10 secondes ─────────────────────────────

    private async Task MeasureLoop()
    {
        // Attendre que l'app soit prête avant la première mesure
        await Task.Delay(2000);

        while (true)
        {
            try
            {
                var (totalMb, usedMb) = SystemMemory.GetPhysicalMemoryMb();
                double availGb = totalMb > 0 ? (totalMb - usedMb) / 1024.0 : 0.0;
                double usedGb  = usedMb / 1024.0;

                // Compter les processus et détecter les relances (Point 3)
                // En Mode Tournoi : on skippe l'énumération pour ne pas perturber le jeu.
                // Process.GetProcesses() déclenche un appel kernel qui peut causer des
                // micro-spikes CPU perceptibles comme stutter.
                int  procCount       = 0;
                var  restartDetected = new List<string>();
                bool skipEnum        = IsTournamentModeActive;

                if (!skipEnum)
                {
                    try
                    {
                        var allProcs = Process.GetProcesses();
                        procCount = allProcs.Length;

                        // Construire la vue des processus à instance unique
                        var currentSingle = allProcs
                            .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() == 1)
                            .Select(g => g.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        // Détecter les relances : était absent au tick précédent, réapparu maintenant
                        foreach (var name in _goneLastTick)
                        {
                            if (currentSingle.Contains(name))
                            {
                                _restartCounts[name] = _restartCounts.GetValueOrDefault(name, 0) + 1;
                                restartDetected.Add(name);
                            }
                        }

                        // Mettre à jour les sets pour le prochain tick
                        _goneLastTick            = new HashSet<string>(_prevSingleInstanceProcs.Except(currentSingle), StringComparer.OrdinalIgnoreCase);
                        _prevSingleInstanceProcs = currentSingle;

                        // Disposer les objets Process pour éviter les fuites
                        foreach (var p in allProcs)
                            try { p.Dispose(); } catch { }
                    }
                    catch { /* Process.GetProcesses() peut échouer si accès refusé */ }
                }

                _measureService.Add(new MeasurePoint
                {
                    Timestamp      = DateTime.UtcNow,
                    RamAvailableGb = availGb,
                    RamUsedGb      = usedGb,
                    ProcessCount   = procCount,
                    ActiveMode     = _currentActiveMode,
                });
            }
            catch { /* Jamais de crash */ }

            await Task.Delay(10_000);
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private void UpdateImprovement(long currentMb)
    {
        if (_baselineRamUsedMb <= 0) return;
        double pct = (_baselineRamUsedMb - currentMb) * 100.0 / _baselineRamUsedMb;
        ImprovementText = pct >= 0.5 ? $"-{pct:F0}%" : "≈ 0%";
    }

    // ── Démarrage automatique Phase 3 ─────────────────────────────────────────

    private static async Task EnsurePhase3RunningAsync()
    {
        await Task.Delay(1500);
        string status = GetServiceStatus();
        if (status == "Actif") return;

        if (status == "Arrêté")
        {
            RunScSilent("start RamAI-Phase3");
            return;
        }

        string? phase3Exe = FindPhase3Exe();
        if (phase3Exe is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(phase3Exe, "--install")
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            })?.WaitForExit();
            await Task.Delay(2000);
            RunScSilent("start RamAI-Phase3");
        }
        catch { }
    }

    private static string? FindPhase3Exe()
    {
        const string InstallPath = @"C:\Program Files\RAM-AI\Phase3\RamAI.Phase3.exe";
        if (File.Exists(InstallPath)) return InstallPath;

        string? appDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (appDir is not null)
        {
            foreach (string relative in new[]
            {
                @"..\..\..\Phase3\bin\Release\net10.0-windows\win-x64\RamAI.Phase3.exe",
                @"..\..\Phase3\RamAI.Phase3.exe",
                @"..\Phase3\RamAI.Phase3.exe",
            })
            {
                string candidate = Path.GetFullPath(Path.Combine(appDir, relative));
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static void RunScSilent(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            })?.WaitForExit(5000);
        }
        catch { }
    }

    private static string GetServiceStatus()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query RamAI-Phase3")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (output.Contains("RUNNING"))       return "Actif";
            if (output.Contains("START_PENDING")) return "Démarrage…";
            if (output.Contains("STOP_PENDING"))  return "Arrêt…";
            if (output.Contains("STOPPED"))       return "Arrêté";
            return "Inconnu";
        }
        catch { return "Non installé"; }
    }

    // ── Commandes ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private static void StartService() => RunSc("start RamAI-Phase3");

    [RelayCommand]
    private static void StopService()  => RunSc("stop RamAI-Phase3");

    [RelayCommand]
    private void OpenLicense()
    {
        var win = new Views.LicenseWindow();
        win.ShowDialog();
    }

    [RelayCommand]
    private void ToggleForceGamingMode()
    {
        ForceGamingMode = !ForceGamingMode;
        try
        {
            if (ForceGamingMode)
            {
                Directory.CreateDirectory(SharedFlagDir);
                File.WriteAllText(ForceFlagPath, "manual");
                IsGamingModeActive = true;
                DetectedGameName   = "Mode Gaming FORCÉ";
                DetectedGameLabel  = "Mode Gaming FORCÉ";
            }
            else
            {
                if (File.Exists(ForceFlagPath))
                {
                    string content = ReadFlagContent();
                    if (content == "manual")
                        File.Delete(ForceFlagPath);
                }
                IsGamingModeActive = false;
                DetectedGameName   = string.Empty;
                DetectedGameLabel  = string.Empty;
            }
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleForceEcoMode()
    {
        ForceEcoMode = !ForceEcoMode;
        try
        {
            if (ForceEcoMode)
            {
                Directory.CreateDirectory(SharedFlagDir);
                File.WriteAllText(EcoFlagPath, "manual");
                IsEcoModeActive = true;
                EcoModeText     = "🔋 Mode Éco forcé (manuel)";
            }
            else
            {
                if (File.Exists(EcoFlagPath)) File.Delete(EcoFlagPath);
                IsEcoModeActive = false;
                EcoModeText     = string.Empty;
            }
        }
        catch { }
    }

    [RelayCommand]
    private void Turbo()
    {
        try
        {
            Directory.CreateDirectory(SharedFlagDir);
            File.WriteAllText(TurboFlagPath, "turbo");
            _stats.TurboUseCount++;
        }
        catch { }
    }

    private void GenerateReportLegacy() { var    now     = DateTime.Now;
        var    elapsed = now - _sessionStart;
            int    hours   = (int)elapsed.TotalHours;
            int    minutes = elapsed.Minutes;

            // ── Infos système ──
            string pcName  = GetPcName();
            string os      = GetOsVersion();
            string cpu     = GetCpuName();
            double totalGb = _totalRamMb > 0 ? _totalRamMb / 1024.0 : 0.0;

            // ── RAM actuelle ──
            var (_, usedNowMb) = SystemMemory.GetPhysicalMemoryMb();
            double ramUsedNowGb = usedNowMb / 1024.0;
            double ramStartGb   = _baselineRamUsedMb / 1024.0;
            double gainPct      = _baselineRamUsedMb > 0
                ? (_baselineRamUsedMb - usedNowMb) * 100.0 / _baselineRamUsedMb : 0.0;

            // ── Mode le plus utilisé aujourd'hui ──
            string topMode = "Aucun";
            int    topTick = 0;
            if (_todayGamingTicks > topTick) { topTick = _todayGamingTicks; topMode = "Gaming"; }
            if (_todayEcoTicks    > topTick) {                               topMode = "Éco";    }

            // ── Pics et stats globales ──
            double peakGb           = _peakTickFreedMb / 1024.0;
            double avgRamPerSession = _stats.TotalSessions > 0
                ? _stats.TotalRamFreedGb / _stats.TotalSessions : 0.0;
            double avgProcsPerSession = _stats.TotalSessions > 0
                ? (double)_stats.TotalProcessesOptimized / _stats.TotalSessions : 0.0;

            // Durée totale (cumulée + session en cours)
            long totalMinutes = _stats.TotalUsageMinutes + (long)elapsed.TotalMinutes;
            int  totalDays    = (int)(totalMinutes / 1440);
            int  totalHours   = (int)((totalMinutes % 1440) / 60);
            int  totalMins    = (int)(totalMinutes % 60);

            string bestSession = _stats.BestSessionRamFreedGb > 0
                ? $"{_stats.BestSessionRamFreedGb:F2} Go récupérés le {_stats.BestSessionDate.ToLocalTime():dd/MM/yyyy}"
                : "—";

            // ── Conclusion ──
            double efficiencyPct = totalGb > 0 ? avgRamPerSession / totalGb * 100.0 : 0.0;
            string verdict =
                _stats.TotalRamFreedGb > 10.0 ? "Performance excellente sur ce système." :
                _stats.TotalRamFreedGb > 5.0  ? "Bonne performance sur ce système."      :
                                                 "Performance correcte sur ce système.";

            // ── Mesures (Point 1) ──
            var measures = _measureService.GetSnapshot();

            // ── Relances détectées (Point 3) ──
            var restarts = _restartCounts
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .ToList();

            // ── Construction du rapport ────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine("================================");
            sb.AppendLine("RAM-AI — Rapport détaillé");
            sb.AppendLine($"Généré le : {now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine("================================");
            sb.AppendLine();

            // SYSTÈME
            sb.AppendLine("── SYSTÈME ──");
            sb.AppendLine($"PC                              : {pcName}");
            sb.AppendLine($"RAM totale                      : {totalGb:F1} Go");
            sb.AppendLine($"OS                              : {os}");
            sb.AppendLine($"Processeur                      : {cpu}");
            sb.AppendLine();

            // RAPPORT DU JOUR
            sb.AppendLine("── RAPPORT DU JOUR ──");
            sb.AppendLine($"Date                            : {now:dd/MM/yyyy}");
            sb.AppendLine($"Durée d'utilisation             : {hours}h {minutes:D2}m");
            sb.AppendLine($"RAM utilisée au démarrage       : {ramStartGb:F2} Go");
            sb.AppendLine($"RAM utilisée actuellement       : {ramUsedNowGb:F2} Go");
            sb.AppendLine($"RAM récupérée aujourd'hui       : {_stats.TodayRamFreedGb:F2} Go");
            sb.AppendLine($"Gain en %                       : {gainPct:F1}%");
            sb.AppendLine($"Processus optimisés aujourd'hui : {_stats.TodayProcessesOptimized:N0}");
            sb.AppendLine($"Mode le plus utilisé aujourd'hui: {topMode}");
            sb.AppendLine($"Pic de RAM récupérée (session)  : {peakGb:F2} Go");
            sb.AppendLine();

            // RAPPORT GLOBAL
            sb.AppendLine("── RAPPORT GLOBAL ──");
            sb.AppendLine($"Date du 1er lancement           : {_stats.FirstLaunch.ToLocalTime():dd/MM/yyyy HH:mm}");
            sb.AppendLine($"Nombre total de sessions        : {_stats.TotalSessions}");
            sb.AppendLine($"Durée totale d'utilisation      : {totalDays}j {totalHours}h {totalMins:D2}m");
            sb.AppendLine($"RAM récupérée au total          : {_stats.TotalRamFreedGb:F2} Go");
            sb.AppendLine($"Processus optimisés au total    : {_stats.TotalProcessesOptimized:N0}");
            sb.AppendLine($"Moyenne RAM / session           : {avgRamPerSession:F2} Go");
            sb.AppendLine($"Moyenne processus / session     : {avgProcsPerSession:F0}");
            sb.AppendLine($"Meilleure session               : {bestSession}");
            sb.AppendLine();

            // MODES UTILISÉS
            sb.AppendLine("── MODES UTILISÉS ──");
            sb.AppendLine($"Mode Gaming     : {_stats.GamingActivations,3} sessions — {_stats.GamingRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode IA         : {_stats.AiActivations,3} sessions — {_stats.AiRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Navigateur : {_stats.BrowserActivations,3} sessions — {_stats.BrowserRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Eco        : {_stats.EcoActivations,3} sessions — {_stats.EcoRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Turbo      : {_stats.TurboUseCount,3} utilisation(s)");
            sb.AppendLine($"Mode Tournoi    : {_stats.TournamentUseCount,3} utilisation(s)");
            sb.AppendLine();

            // Point 3 : PROCESSUS RELANCÉS
            if (restarts.Count > 0)
            {
                sb.AppendLine("── ATTENTION : PROCESSUS RELANCÉS PENDANT LA SESSION ──");
                foreach (var (name, count) in restarts)
                    sb.AppendLine($"  {name,-40} s'est relancé {count} fois");
                sb.AppendLine("  Note : les relances ne sont PAS comptées comme des optimisations.");
                sb.AppendLine();
            }

            // Point 1 : TABLEAU DE MESURES (toutes les 10 s)
            sb.AppendLine("── MESURES TOUTES LES 10 SECONDES ──");
            if (measures.Count == 0)
            {
                sb.AppendLine("  Aucune mesure disponible (session trop courte).");
            }
            else
            {
                // Si plus de 60 entrées, afficher les 60 dernières
                const int MaxDisplay = 60;
                List<MeasurePoint> displayed;
                if (measures.Count > MaxDisplay)
                {
                    sb.AppendLine($"  (Affichage des {MaxDisplay} dernières mesures sur {measures.Count} — voir le CSV pour la liste complète)");
                    displayed = measures.Skip(measures.Count - MaxDisplay).ToList();
                }
                else
                {
                    displayed = measures;
                }

                sb.AppendLine($"  {"Heure",-10} {"RAM dispo",-12} {"RAM util.",-12} {"Processus",-12} Mode");
                sb.AppendLine($"  {new string('-', 9),-10} {new string('-', 10),-12} {new string('-', 10),-12} {new string('-', 9),-12} {new string('-', 12)}");
                foreach (var m in displayed)
                {
                    double avail = m.RamAvailableGb;
                    string availStr = $"{avail:F2} Go";
                    string usedStr  = $"{m.RamUsedGb:F2} Go";
                    sb.AppendLine($"  {m.Timestamp.ToLocalTime():HH:mm:ss,-10} {availStr,-12} {usedStr,-12} {m.ProcessCount,-12} {m.ActiveMode}");
                }
            }
            sb.AppendLine();

            // Point 2 : MÉTHODE D'OPTIMISATION
            sb.AppendLine("── MÉTHODE D'OPTIMISATION ──");
            sb.AppendLine("  RAM-AI utilise l'API Windows SetProcessWorkingSetSize() pour");
            sb.AppendLine("  demander à Windows de compresser la mémoire des processus inactifs");
            sb.AppendLine("  vers le fichier de pagination (pagefile.sys).");
            sb.AppendLine("  Les processus ne sont PAS tués — ils restent actifs mais leur");
            sb.AppendLine("  mémoire inactive est libérée pour les processus prioritaires.");
            sb.AppendLine("  La RAM récupérée = mémoire inactive compressée rendue disponible.");
            sb.AppendLine("  Dès qu'un processus en a besoin, Windows recharge sa mémoire depuis");
            sb.AppendLine("  le fichier de pagination (temps de réponse ~ms).");
            sb.AppendLine();

            // Point 4 : MODE COMPARAISON
            sb.AppendLine("── MODE COMPARAISON (tester l'efficacité) ──");
            sb.AppendLine("  Pour comparer les performances avec et sans RAM-AI :");
            sb.AppendLine();
            sb.AppendLine("  SANS RAM-AI (référence) :");
            sb.AppendLine("  1. Redémarrez votre PC");
            sb.AppendLine("  2. Lancez votre activité habituelle pendant 10 minutes SANS RAM-AI");
            sb.AppendLine("  3. Ouvrez le Gestionnaire des tâches → Performances → Ouvrir le");
            sb.AppendLine("     Moniteur de ressources → Mémoire → Exporter");
            sb.AppendLine("     (ou utilisez le CSV généré par RAM-AI comme référence zéro)");
            sb.AppendLine();
            sb.AppendLine("  AVEC RAM-AI :");
            sb.AppendLine("  4. Redémarrez, relancez RAM-AI");
            sb.AppendLine("  5. Effectuez la même activité pendant 10 minutes AVEC RAM-AI actif");
            sb.AppendLine("  6. Cliquez sur 📄 Rapport pour générer le CSV RAM-AI");
            sb.AppendLine();
            sb.AppendLine("  COMPARAISON :");
            sb.AppendLine("  7. Ouvrez les deux CSV dans Excel");
            sb.AppendLine("  8. Comparez la colonne 'RAM disponible' : plus elle est haute AVEC");
            sb.AppendLine("     RAM-AI, plus l'optimisation est efficace sur votre configuration.");
            sb.AppendLine("  9. Comparez aussi le nombre de processus actifs entre les deux CSV.");
            sb.AppendLine();

            // CONCLUSION
            sb.AppendLine("── CONCLUSION ──");
            sb.AppendLine($"  RAM-AI a récupéré {_stats.TotalRamFreedGb:F2} Go depuis le {_stats.FirstLaunch.ToLocalTime():dd/MM/yyyy}.");
            sb.AppendLine($"  Soit l'équivalent de {efficiencyPct:F1}% de votre RAM totale libérée en moyenne.");
            sb.AppendLine($"  {verdict}");
            sb.AppendLine();
            sb.AppendLine("================================");
            sb.AppendLine($"RAM-AI v1.0 — {now:dd/MM/yyyy}");
            sb.AppendLine("================================");

            // ── Écrire le TXT + CSV + ouvrir notepad ──────────────────────────
            string dir     = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RAM-AI");
            string stamp   = now.ToString("yyyyMMdd_HHmmss");
            string txtPath = Path.Combine(dir, $"rapport_{stamp}.txt");
            string csvPath = Path.Combine(dir, $"mesures_{stamp}.csv");

            Directory.CreateDirectory(dir);
            File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);

            // Exporter aussi le CSV (Point 1)
            _measureService.ExportCsv(csvPath);

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{txtPath}\"")
            {
                UseShellExecute = true,
            });
    }

    // ── Informations système ──────────────────────────────────────────────────

    private static string GetPcName()
    {
        try { return Environment.MachineName; }
        catch { return "Inconnu"; }
    }

    private static string GetOsVersion()
    {
        try
        {
            var v    = Environment.OSVersion.Version;
            string name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} (Build {v.Build})";
        }
        catch { return "Windows"; }
    }

    private static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Inconnu";
        }
        catch { return "Inconnu"; }
    }

    // ── Sauvegarde à la fermeture ─────────────────────────────────────────────

    /// <summary>
    /// Finalise les stats (durée, meilleure session), persiste stats.json et measures.json.
    /// Appelé par App.xaml.cs dans ExitApp().
    /// </summary>
    public void SaveStats()
    {
        try
        {
            _stats.TotalUsageMinutes += (long)(DateTime.Now - _sessionStart).TotalMinutes;

            double sessionGb = _sessionFreedMb / 1024.0;
            if (sessionGb > _stats.BestSessionRamFreedGb)
            {
                _stats.BestSessionRamFreedGb = sessionGb;
                _stats.BestSessionDate       = DateTime.UtcNow;
            }
        }
        catch { }

        _statsService.Save(_stats);
        _measureService.SaveToJson();   // Persiste les mesures (Point 1)
    }

    private static void RunSc(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }
}

// ── Lecture RAM physique via GlobalMemoryStatusEx ─────────────────────────────

internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    internal static (long TotalMb, long UsedMb) GetPhysicalMemoryMb()
    {
        var ms = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };
        if (!GlobalMemoryStatusEx(ref ms)) return (0, 0);
        long totalMb = (long)(ms.ullTotalPhys / (1024UL * 1024UL));
        long availMb = (long)(ms.ullAvailPhys / (1024UL * 1024UL));
        return (totalMb, totalMb - availMb);
    }
}
