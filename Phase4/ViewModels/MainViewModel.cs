using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;

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
    [ObservableProperty] private string _serviceStatus    = "Inconnu";
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

    // ── Baseline RAM ──────────────────────────────────────────────────────────
    private long _baselineRamUsedMb;

    // ── Stats persistées ──────────────────────────────────────────────────────
    private readonly StatsService _statsService = new();
    private readonly StatsData    _stats;
    private readonly DateTime     _sessionStart = DateTime.Now;

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

        var (_, usedMb) = SystemMemory.GetPhysicalMemoryMb();
        _baselineRamUsedMb = usedMb;
        CurrentRamUsedGb   = usedMb / 1024.0;

        // Charger les stats persistées et réinitialiser les compteurs du jour si besoin
        _stats = _statsService.Load();
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        if (_stats.TodayDate != todayKey)
        {
            _stats.TodayDate               = todayKey;
            _stats.TodayRamFreedGb         = 0.0;
            _stats.TodayProcessesOptimized = 0;
        }
        // Incrémenter le compteur de sessions
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
            if (entry.PhysicalMbFreed >= 1)
            {
                TotalMbSaved   += entry.PhysicalMbFreed;
                TotalRamFreedGb = TotalMbSaved / 1024.0;

                // Accumuler dans les stats persistées
                double freedGb = entry.PhysicalMbFreed / 1024.0;
                _stats.TotalRamFreedGb   += freedGb;
                _stats.TodayRamFreedGb   += freedGb;
            }
            long procsThisTick = entry.ColdEvicted + entry.AiProcessesOptimized;
            ProcessesOptimized            += procsThisTick;
            _stats.TotalProcessesOptimized += procsThisTick;
            _stats.TodayProcessesOptimized += procsThisTick;
            LastUpdateText      = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            IsGamingModeActive = entry.IsGamingMode || ForceGamingMode;

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
            if (entry.IsBrowserMode && !IsGamingModeActive && !entry.IsAiMode)
            {
                IsBrowserModeActive = true;
                BrowserInfoText     = $"{entry.BrowserName} — {entry.BrowserTabsOptimized} onglet(s) optimisé(s)";
            }
            else if (!entry.IsBrowserMode)
            {
                IsBrowserModeActive = false;
                BrowserInfoText     = string.Empty;
            }

            // ── Mode IA ──
            if (entry.IsAiMode && !IsGamingModeActive)
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

            // ── Mode Éco ──
            if (entry.IsEcoMode)
            {
                IsEcoModeActive = true;
                EcoModeText     = "🔋 Mode Éco actif";
            }
            else
            {
                IsEcoModeActive = false;
                EcoModeText     = string.Empty;
            }

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
        }
        catch { }
    }

    // ── Rapport ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Génère un fichier rapport.txt dans C:\ProgramData\RAM-AI\ et l'ouvre
    /// dans le Bloc-notes Windows. Aucune fenêtre supplémentaire dans l'app.
    /// </summary>
    [RelayCommand]
    private void GenerateReport()
    {
        try
        {
            var now      = DateTime.Now;
            var elapsed  = now - _sessionStart;
            int hours    = (int)elapsed.TotalHours;
            int minutes  = elapsed.Minutes;

            double avgRamPerSession = _stats.TotalSessions > 0
                ? _stats.TotalRamFreedGb / _stats.TotalSessions
                : 0.0;

            string report =
                $"================================\r\n" +
                $"RAM-AI — Rapport\r\n" +
                $"Généré le : {now:dd/MM/yyyy HH:mm:ss}\r\n" +
                $"================================\r\n" +
                $"\r\n" +
                $"── RAPPORT DU JOUR ──\r\n" +
                $"RAM récupérée aujourd'hui       : {_stats.TodayRamFreedGb:F2} Go\r\n" +
                $"Processus optimisés aujourd'hui : {_stats.TodayProcessesOptimized:N0}\r\n" +
                $"Durée d'utilisation aujourd'hui : {hours}h {minutes:D2}m\r\n" +
                $"\r\n" +
                $"── RAPPORT GLOBAL (depuis le 1er lancement) ──\r\n" +
                $"RAM récupérée au total          : {_stats.TotalRamFreedGb:F2} Go\r\n" +
                $"Processus optimisés au total    : {_stats.TotalProcessesOptimized:N0}\r\n" +
                $"Nombre de sessions              : {_stats.TotalSessions}\r\n" +
                $"Moyenne RAM / session           : {avgRamPerSession:F2} Go\r\n" +
                $"Date du 1er lancement           : {_stats.FirstLaunch.ToLocalTime():dd/MM/yyyy HH:mm}\r\n" +
                $"\r\n" +
                $"================================\r\n" +
                $"RAM-AI v1.0 — {now:dd/MM/yyyy}\r\n" +
                $"================================\r\n";

            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RAM-AI");
            string path = Path.Combine(dir, $"rapport_{now:yyyyMMdd_HHmmss}.txt");

            Directory.CreateDirectory(dir);
            File.WriteAllText(path, report, System.Text.Encoding.UTF8);

            // Ouvrir dans le Bloc-notes Windows
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// <summary>Sauvegarde les stats persistées. Appelé par App.xaml.cs à la fermeture.</summary>
    public void SaveStats() => _statsService.Save(_stats);

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
