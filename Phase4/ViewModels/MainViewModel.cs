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

    // ── Mode Navigateur ───────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isBrowserModeActive;
    [ObservableProperty] private string _browserInfoText   = string.Empty;

    // ── Mode IA ───────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isAiModeActive;
    [ObservableProperty] private string _aiInfoText        = string.Empty;

    // ── Mode Éco (batterie) ───────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEcoModeActive;
    [ObservableProperty] private string _ecoModeText       = string.Empty;

    // ── Ultra ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isUltraModeActive;
    [ObservableProperty] private bool   _isTournamentModeActive;
    [ObservableProperty] private string _vramInfoText       = string.Empty;

    // ── Compteurs IA cumulatifs ───────────────────────────────────────────────
    private int _totalAiProcessesEvicted;

    // ── RAM au démarrage ─────────────────────────────────────────────────────
    private long _baselineRamUsedMb;
    private long _totalRamMb;

    // ── Stats persistées ──────────────────────────────────────────────────────
    private readonly StatsService _statsService = new();
    private readonly StatsData    _stats;
    private readonly DateTime     _sessionStart = DateTime.Now;

    // ── Tracking des transitions de mode (pour compter les activations) ────────
    // Chaque champ passe true→false→true à chaque fois que le mode s'active.
    private bool _prevGaming;
    private bool _prevAi;
    private bool _prevBrowser;
    private bool _prevEco;

    // ── Compteurs de ticks par mode aujourd'hui (pour "mode le plus utilisé") ──
    private int _todayGamingTicks;
    private int _todayAiTicks;
    private int _todayBrowserTicks;
    private int _todayEcoTicks;

    // ── Pic de RAM libérée en un seul tick (cette session) ────────────────────
    private long _peakTickFreedMb;

    // ── RAM libérée cette session (pour "meilleure session") ─────────────────
    private long _sessionFreedMb;

    // ── Dossier partagé Phase3 ↔ Phase4 ───────────────────────────────────────
    private static readonly string SharedFlagDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI");

    private static readonly string ForceFlagPath      = Path.Combine(SharedFlagDir, "gaming_mode.force");
    private static readonly string TurboFlagPath      = Path.Combine(SharedFlagDir, "turbo_mode.force");
    private static readonly string TournamentFlagPath = Path.Combine(SharedFlagDir, "tournament_mode.force");

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
        var _  = RefreshLoop();
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

                // Attribuer à un mode (priorité : Gaming > IA > Navigateur > Éco)
                if      (entry.IsGamingMode || ForceGamingMode) _stats.GamingRamFreedGb  += freedGb;
                else if (entry.IsAiMode)                        _stats.AiRamFreedGb      += freedGb;
                else if (entry.IsBrowserMode)                   _stats.BrowserRamFreedGb += freedGb;
                else if (entry.IsEcoMode)                       _stats.EcoRamFreedGb     += freedGb;
            }

            // ── Processus ──
            long procsThisTick = entry.ColdEvicted + entry.AiProcessesOptimized;
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

            // ── Mode Navigateur ──
            bool browserNow = entry.IsBrowserMode && !gamingNow && !entry.IsAiMode;
            if (browserNow)
            {
                IsBrowserModeActive = true;
                BrowserInfoText     = $"{entry.BrowserName} — {entry.BrowserTabsOptimized} onglet(s) optimisé(s)";
            }
            else if (!entry.IsBrowserMode)
            {
                IsBrowserModeActive = false;
                BrowserInfoText     = string.Empty;
            }
            if (!_prevBrowser && browserNow) _stats.BrowserActivations++;
            _prevBrowser = browserNow;
            if (browserNow) _todayBrowserTicks++;

            // ── Mode IA ──
            bool aiNow = entry.IsAiMode && !gamingNow;
            if (aiNow)
            {
                _totalAiProcessesEvicted += entry.AiProcessesOptimized;
                IsAiModeActive = true;
                AiInfoText     = $"{entry.AiName} — {_totalAiProcessesEvicted} processus optimisé(s)";
            }
            else if (!entry.IsAiMode)
            {
                IsAiModeActive = false;
                AiInfoText     = string.Empty;
            }
            if (!_prevAi && aiNow) _stats.AiActivations++;
            _prevAi = aiNow;
            if (aiNow) _todayAiTicks++;

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

            // ── Ultra ──
            IsTournamentModeActive = entry.IsTournamentMode;
            VramInfoText = entry.VramMb > 0
                ? $"VRAM : {entry.VramMb} Mo"
                : string.Empty;
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

    // ── Boucle de rafraîchissement toutes les 2 s ─────────────────────────────

    private async Task RefreshLoop()
    {
        while (true)
        {
            var    status      = GetServiceStatus();
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

            await Task.Delay(2000);
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

    // ── Rapport détaillé ──────────────────────────────────────────────────────

    [RelayCommand]
    private void GenerateReport()
    {
        try
        {
            var    now     = DateTime.Now;
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
            double ramUsedNowGb  = usedNowMb / 1024.0;
            double ramStartGb    = _baselineRamUsedMb / 1024.0;
            double gainPct       = _baselineRamUsedMb > 0
                ? (_baselineRamUsedMb - usedNowMb) * 100.0 / _baselineRamUsedMb
                : 0.0;

            // ── Mode le plus utilisé aujourd'hui ──
            string topMode = "Aucun";
            int    topTick = 0;
            if (_todayGamingTicks  > topTick) { topTick = _todayGamingTicks;  topMode = "Gaming";     }
            if (_todayAiTicks      > topTick) { topTick = _todayAiTicks;      topMode = "IA";         }
            if (_todayBrowserTicks > topTick) { topTick = _todayBrowserTicks; topMode = "Navigateur"; }
            if (_todayEcoTicks     > topTick) {                               topMode = "Éco";        }

            // ── Pic session ──
            double peakGb = _peakTickFreedMb / 1024.0;

            // ── Stats globales ──
            double avgRamPerSession  = _stats.TotalSessions > 0
                ? _stats.TotalRamFreedGb / _stats.TotalSessions : 0.0;
            double avgProcsPerSession = _stats.TotalSessions > 0
                ? (double)_stats.TotalProcessesOptimized / _stats.TotalSessions : 0.0;

            // Durée totale (cumulée + session en cours)
            long   totalMinutes   = _stats.TotalUsageMinutes + (long)elapsed.TotalMinutes;
            int    totalDays      = (int)(totalMinutes / 1440);
            int    totalHours     = (int)((totalMinutes % 1440) / 60);
            int    totalMins      = (int)(totalMinutes % 60);

            // Meilleure session (hors session courante déjà persistée)
            string bestSession = _stats.BestSessionRamFreedGb > 0
                ? $"{_stats.BestSessionRamFreedGb:F2} Go récupérés le {_stats.BestSessionDate.ToLocalTime():dd/MM/yyyy}"
                : "—";

            // ── Conclusion ──
            double efficiencyPct = totalGb > 0
                ? avgRamPerSession / totalGb * 100.0 : 0.0;
            string verdict =
                _stats.TotalRamFreedGb > 10.0 ? "Performance excellente sur ce système." :
                _stats.TotalRamFreedGb > 5.0  ? "Bonne performance sur ce système."      :
                                                 "Performance correcte sur ce système.";

            var sb = new StringBuilder();
            sb.AppendLine("================================");
            sb.AppendLine("RAM-AI — Rapport détaillé");
            sb.AppendLine($"Généré le : {now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine("================================");
            sb.AppendLine();
            sb.AppendLine("── SYSTÈME ──");
            sb.AppendLine($"PC                              : {pcName}");
            sb.AppendLine($"RAM totale                      : {totalGb:F1} Go");
            sb.AppendLine($"OS                              : {os}");
            sb.AppendLine($"Processeur                      : {cpu}");
            sb.AppendLine();
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
            sb.AppendLine("── MODES UTILISÉS ──");
            sb.AppendLine($"Mode Gaming     : {_stats.GamingActivations,3} sessions — {_stats.GamingRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode IA         : {_stats.AiActivations,3} sessions — {_stats.AiRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Navigateur : {_stats.BrowserActivations,3} sessions — {_stats.BrowserRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Eco        : {_stats.EcoActivations,3} sessions — {_stats.EcoRamFreedGb:F2} Go récupérés");
            sb.AppendLine($"Mode Turbo      : {_stats.TurboUseCount,3} utilisation(s)");
            sb.AppendLine($"Mode Tournoi    : {_stats.TournamentUseCount,3} utilisation(s)");
            sb.AppendLine();
            sb.AppendLine("── CONCLUSION ──");
            sb.AppendLine($"RAM-AI a récupéré {_stats.TotalRamFreedGb:F2} Go depuis le {_stats.FirstLaunch.ToLocalTime():dd/MM/yyyy}.");
            sb.AppendLine($"Soit l'équivalent de {efficiencyPct:F1}% de votre RAM totale libérée en moyenne.");
            sb.AppendLine(verdict);
            sb.AppendLine();
            sb.AppendLine("================================");
            sb.AppendLine($"RAM-AI v1.0 — {now:dd/MM/yyyy}");
            sb.AppendLine("================================");

            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RAM-AI");
            string path = Path.Combine(dir, $"rapport_{now:yyyyMMdd_HHmmss}.txt");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { }
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

    // ── Sauvegarde des stats à la fermeture ───────────────────────────────────

    /// <summary>
    /// Finalise les stats de la session (durée, meilleure session) et persiste.
    /// Appelé par App.xaml.cs dans ExitApp().
    /// </summary>
    public void SaveStats()
    {
        try
        {
            // Ajouter la durée de cette session
            _stats.TotalUsageMinutes += (long)(DateTime.Now - _sessionStart).TotalMinutes;

            // Mettre à jour la meilleure session si besoin
            double sessionGb = _sessionFreedMb / 1024.0;
            if (sessionGb > _stats.BestSessionRamFreedGb)
            {
                _stats.BestSessionRamFreedGb = sessionGb;
                _stats.BestSessionDate       = DateTime.UtcNow;
            }
        }
        catch { }

        _statsService.Save(_stats);
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
