using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Application        = System.Windows.Application;
using MessageBox         = System.Windows.MessageBox;
using MessageBoxButton   = System.Windows.MessageBoxButton;
using MessageBoxImage    = System.Windows.MessageBoxImage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;

namespace RamAI.Phase4.ViewModels;


public sealed partial class MainViewModel : ObservableObject
{
    // ── Mode Bêta testeur ─────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isBetaMode;
    // "🧪 Version testeur — expire le 04/07/2025"
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

    // ── Navigation onglets ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    private bool _showBenchmarks;

    public bool ShowDashboard => !ShowBenchmarks;

    // ── Onglet Benchmarks ─────────────────────────────────────────────────────
    public BenchmarkViewModel BenchmarkVm { get; }

    // ── Compteurs IA cumulatifs (non-observables, alimentent AiInfoText) ─────
    private int _totalAiProcessesEvicted;

    // ── Baseline RAM + horodatage session ────────────────────────────────────
    private long     _baselineRamUsedMb;
    private DateTime _sessionStart = DateTime.Now;

    // ── Dossier partagé Phase3 ↔ Phase4 ───────────────────────────────────────
    // C:\ProgramData\RAM-AI\ — accessible en lecture/écriture par :
    //   • Phase3 (service Windows, compte LocalSystem)
    //   • Phase4 (application utilisateur)
    //   • En dev ET après dotnet publish, sans résolution de chemin fragile.
    private static readonly string SharedFlagDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI");

    // gaming_mode.force : Phase4 écrit "manual" pour forcer, Phase3 écrit "auto" pour détection auto.
    // turbo_mode.force  : Phase4 écrit "turbo", Phase3 exécute la passe et supprime le fichier.
    private static readonly string ForceFlagPath = Path.Combine(SharedFlagDir, "gaming_mode.force");
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
        // On ne lit que "manual" — le flag "auto" écrit par Phase3 n'est pas
        // un mode forcé utilisateur ; l'état gaming vient des entrées de log.
        if (ReadFlagContent() == "manual")
        {
            ForceGamingMode    = true;
            IsGamingModeActive = true;
            DetectedGameName   = "Mode Gaming FORCÉ";
            DetectedGameLabel  = "Mode Gaming FORCÉ";
        }

        // Créer le dossier partagé si nécessaire (même dossier que Phase3 surveille)
        try { Directory.CreateDirectory(SharedFlagDir); } catch { }
        Console.WriteLine($"[RAM-AI Phase4] SharedFlagDir  : {SharedFlagDir}");
        Console.WriteLine($"[RAM-AI Phase4] ForceFlagPath  : {ForceFlagPath}");
        Console.WriteLine($"[RAM-AI Phase4] TurboFlagPath  : {TurboFlagPath}");

        // Bandeau bêta
        if (licenseService.Current.Tier == Models.LicenseTier.Beta && licenseService.BetaExpiryDate.HasValue)
        {
            IsBetaMode      = true;
            BetaExpiryText  = $"🧪 Version testeur — expire le {licenseService.BetaExpiryDate.Value.ToLocalTime():dd/MM/yyyy}";
        }

        var (_, usedMb) = SystemMemory.GetPhysicalMemoryMb();
        _baselineRamUsedMb = usedMb;
        CurrentRamUsedGb   = usedMb / 1024.0;

        BenchmarkVm = new BenchmarkViewModel(new BenchmarkService(), logWatcher);

        var __ = EnsurePhase3RunningAsync();
        var _  = RefreshLoop();
    }

    [RelayCommand]
    private void NavigateToDashboard()  => ShowBenchmarks = false;

    [RelayCommand]
    private void NavigateToBenchmarks() => ShowBenchmarks = true;

    // ── Lecture du contenu du flag (distingue "manual" / "auto" / vide) ────────

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
    // C'est la SOURCE DE VÉRITÉ pour l'état gaming auto-détecté.

    private void OnNewEntry(EventEntry entry)
    {
        Console.WriteLine($"[Phase4] OnNewEntry : IsGamingMode={entry.IsGamingMode} | IsBrowserMode={entry.IsBrowserMode} | IsAiMode={entry.IsAiMode} | AiName='{entry.AiName}' | AiProcessesOptimized={entry.AiProcessesOptimized} | MbSaved={entry.MbSaved} | PhysicalMbFreed={entry.PhysicalMbFreed}");
        if (entry.IsAiMode)
            Console.WriteLine($"[Phase4] IA processus reçus : {entry.AiProcessesOptimized}");

        Application.Current.Dispatcher.Invoke(() =>
        {
            // Utiliser la RAM physique réellement libérée (mesurée avant/après par Phase3).
            // Ne pas accumuler si le gain est inférieur à 10 Mo (bruit de mesure).
            if (entry.PhysicalMbFreed >= 1)
            {
                TotalMbSaved   += entry.PhysicalMbFreed;
                TotalRamFreedGb = TotalMbSaved / 1024.0;
            }
            ProcessesOptimized += entry.ColdEvicted + entry.AiProcessesOptimized;
            LastUpdateText      = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            // Actif si Phase3 signale gaming OU si l'utilisateur a forcé manuellement
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

            // ── Mode Navigateur (affiché seulement si gaming ET IA inactifs) ──
            if (entry.IsBrowserMode && !IsGamingModeActive && !entry.IsAiMode)
            {
                IsBrowserModeActive = true;
                BrowserInfoText     = $"{entry.BrowserName} — {entry.BrowserTabsOptimized} onglet(s) optimisé(s)";
                Console.WriteLine($"[Phase4] BROWSER MODE ACTIVÉ → BrowserInfoText='{BrowserInfoText}'");
            }
            else if (!entry.IsBrowserMode)
            {
                IsBrowserModeActive = false;
                BrowserInfoText     = string.Empty;
            }

            // ── Mode IA (priorité sur navigateur, seulement si gaming inactif) ──
            if (entry.IsAiMode && !IsGamingModeActive)
            {
                _totalAiProcessesEvicted += entry.AiProcessesOptimized;
                IsAiModeActive = true;
                AiInfoText     = $"{entry.AiName} — {_totalAiProcessesEvicted} processus optimisé(s)";
                Console.WriteLine($"[Phase4] AI MODE ACTIVÉ → total évincés={_totalAiProcessesEvicted} (ce tick={entry.AiProcessesOptimized})");
            }
            else if (!entry.IsAiMode)
            {
                IsAiModeActive = false;
                AiInfoText     = string.Empty;
            }

            // ── Mode Éco (batterie) ───────────────────────────────────────────
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

            // ── Ultra ─────────────────────────────────────────────────────────
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
                Console.WriteLine("[RAM-AI] 🏆 TOURNOI ACTIVÉ");
            }
            else
            {
                if (File.Exists(TournamentFlagPath)) File.Delete(TournamentFlagPath);
                Console.WriteLine("[RAM-AI] 🏆 TOURNOI DÉSACTIVÉ");
            }
        }
        catch { }
    }

    // ── Boucle de rafraîchissement toutes les 2 s ─────────────────────────────

    private async Task RefreshLoop()
    {
        while (true)
        {
            var   status      = GetServiceStatus();
            var   (_, usedMb) = SystemMemory.GetPhysicalMemoryMb();
            // Lire le CONTENU du flag, pas juste son existence.
            // "manual" = forcé par l'utilisateur via dashboard
            // "auto"   = écrit par Phase3 lors d'une détection automatique → NE PAS toucher ForceGamingMode
            // ""       = pas de flag
            string flagContent = ReadFlagContent();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                ServiceStatus    = status;
                CurrentRamUsedGb = usedMb / 1024.0;
                UpdateImprovement(usedMb);

                // Synchronisation du flag "manual" seulement :
                // • si le fichier passe à "manual" sans que l'UI le sache → activer ForceGamingMode
                // • si le fichier n'est plus "manual" (supprimé ou "auto") → désactiver ForceGamingMode
                if (flagContent == "manual" && !ForceGamingMode)
                {
                    ForceGamingMode    = true;
                    IsGamingModeActive = true;
                    DetectedGameName   = "Mode Gaming FORCÉ";
                    DetectedGameLabel  = "Mode Gaming FORCÉ";
                }
                else if (flagContent != "manual" && ForceGamingMode)
                {
                    // Le flag "manual" a disparu (supprimé par l'utilisateur ou Phase3)
                    // → désactiver le mode forcé ET masquer la bannière immédiatement.
                    ForceGamingMode    = false;
                    IsGamingModeActive = false;
                    DetectedGameName   = string.Empty;
                    DetectedGameLabel  = string.Empty;
                }
                // "auto" : on ne touche pas ForceGamingMode, IsGamingModeActive est géré par OnNewEntry
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

    /// <summary>
    /// Bascule le mode Gaming FORCÉ. Écrit ou supprime gaming_mode.force avec "manual".
    /// Indépendant de la détection automatique de Phase3.
    /// </summary>
    [RelayCommand]
    private void ToggleForceGamingMode()
    {
        ForceGamingMode = !ForceGamingMode;
        try
        {
            if (ForceGamingMode)
            {
                // Forcer le mode gaming : écrire "manual" dans le flag
                Directory.CreateDirectory(SharedFlagDir);
                File.WriteAllText(ForceFlagPath, "manual");
                Console.WriteLine($"[RAM-AI] GAMING FORCÉ ACTIVÉ — flag : {ForceFlagPath}");
                IsGamingModeActive = true;
                DetectedGameName   = "Mode Gaming FORCÉ";
                DetectedGameLabel  = "Mode Gaming FORCÉ";
            }
            else
            {
                // Désactiver le mode forcé : supprimer le flag
                // Phase3 reviendra en mode normal dès le prochain tick (800 ms)
                if (File.Exists(ForceFlagPath))
                {
                    string content = ReadFlagContent();
                    // Ne supprimer que si c'est notre flag "manual", pas un flag "auto" de Phase3
                    if (content == "manual")
                    {
                        File.Delete(ForceFlagPath);
                        Console.WriteLine($"[RAM-AI] GAMING FORCÉ DÉSACTIVÉ — flag supprimé : {ForceFlagPath}");
                    }
                }
                // Masquer la bannière immédiatement.
                // Si Phase3 détecte encore un jeu de manière autonome,
                // le prochain OnNewEntry remettra IsGamingModeActive = true avec le bon nom.
                IsGamingModeActive = false;
                DetectedGameName   = string.Empty;
                DetectedGameLabel  = string.Empty;
            }
        }
        catch { }
    }

    /// <summary>
    /// Déclenche une passe Turbo one-shot via turbo_mode.force.
    /// Phase3 vide le working set de tous les processus au prochain tick puis supprime le flag.
    /// </summary>
    [RelayCommand]
    private void Turbo()
    {
        try
        {
            Directory.CreateDirectory(SharedFlagDir);
            File.WriteAllText(TurboFlagPath, "turbo");
            Console.WriteLine($"[RAM-AI] TURBO LANCÉ — flag : {TurboFlagPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RAM-AI] TURBO ERREUR : {ex.Message}");
        }
    }

    /// <summary>
    /// Génère rapport.txt dans C:\ProgramData\RAM-AI\ avec les métriques de la session courante.
    /// </summary>
    [RelayCommand]
    private void GenerateReport()
    {
        try
        {
            Directory.CreateDirectory(SharedFlagDir);

            var    now      = DateTime.Now;
            var    duration = now - _sessionStart;
            string mode     = IsTournamentModeActive ? "Tournoi"
                            : IsGamingModeActive     ? "Gaming"
                            : IsAiModeActive         ? "IA"
                            : IsBrowserModeActive    ? "Navigateur"
                            : IsEcoModeActive        ? "Éco"
                            :                          "Standard";

            var (totalMb, usedMb) = SystemMemory.GetPhysicalMemoryMb();
            double totalGb = totalMb / 1024.0;
            double usedGb  = usedMb  / 1024.0;

            string vram = string.IsNullOrEmpty(VramInfoText) ? "N/A" : VramInfoText;

            string content =
                $"╔══════════════════════════════════════════════════════╗\n" +
                $"║              RAM-AI — Rapport de session              ║\n" +
                $"╚══════════════════════════════════════════════════════╝\n" +
                $"\n" +
                $"Date et heure          : {now:dd/MM/yyyy HH:mm:ss}\n" +
                $"Durée de la session    : {(int)duration.TotalHours:D2}h {duration.Minutes:D2}m {duration.Seconds:D2}s\n" +
                $"\n" +
                $"── Mémoire ─────────────────────────────────────────────\n" +
                $"RAM totale             : {totalGb:F1} Go\n" +
                $"RAM utilisée (actuel)  : {usedGb:F2} Go\n" +
                $"RAM récupérée (cumul)  : {TotalRamFreedGb:F2} Go\n" +
                $"\n" +
                $"── Optimisation ────────────────────────────────────────\n" +
                $"Processus optimisés    : {ProcessesOptimized:N0}\n" +
                $"Mode actif             : {mode}\n" +
                $"Licence                : {LicenseTierLabel}\n" +
                $"\n" +
                $"── GPU ─────────────────────────────────────────────────\n" +
                $"VRAM                   : {vram}\n" +
                $"\n" +
                $"────────────────────────────────────────────────────────\n" +
                $"Généré par RAM-AI v1.0 — {now:yyyy-MM-dd HH:mm:ss}\n";

            string fileName = $"rapport_{now:yyyy-MM-dd_HHmmss}.txt";
            string path     = Path.Combine(SharedFlagDir, fileName);
            File.WriteAllText(path, content, System.Text.Encoding.UTF8);

            Console.WriteLine($"[RAM-AI] Rapport généré : {path}");

            // Ouvrir le fichier dans le Bloc-notes
            try { Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RAM-AI] Erreur génération rapport : {ex.Message}");
            MessageBox.Show($"Impossible de générer le rapport :\n{ex.Message}",
                "RAM-AI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
