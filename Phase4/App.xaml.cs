using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;
using RamAI.Phase4.ViewModels;
using RamAI.Phase4.Views;

namespace RamAI.Phase4;

public partial class App : Application
{
    // ── Singletons accessibles depuis toute l'application ────────────────────
    public static LicenseService    LicenseService { get; } = new();
    public static LogWatcherService LogWatcher     { get; private set; } = null!;
    public static MainViewModel     MainVm         { get; private set; } = null!;

    private NotifyIconService?        _tray;
    private MainWindow?               _mainWindow;
    private ForegroundWatcherService? _foregroundWatcher;

    private static readonly string SharedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAM-AI");

    private static readonly string LogPath      = Path.Combine(SharedDir, "events.log");
    private static readonly string StatsPath    = Path.Combine(SharedDir, "stats.json");
    private static readonly string MeasuresPath = Path.Combine(SharedDir, "measures.json");

    // ── Démarrage ─────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Gestionnaire global d'exceptions non gérées ───────────────────────
        // Capture les exceptions WPF (thread UI) et thread-pool qui auraient
        // sinon fermé le process silencieusement.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        // L'application vit dans la barre système même si la fenêtre est fermée
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ── Nettoyage préventif des fichiers JSON ─────────────────────────────
        // Si un crash a laissé un fichier JSON corrompu (JSON tronqué/invalide),
        // le supprimer avant d'initialiser les services pour garantir le démarrage.

        SanitizeJsonFile(StatsPath);
        SanitizeJsonFile(MeasuresPath);

        try { InitializeApp(); }
        catch (Exception ex) { ShowFatalError(ex, "initialisation"); }
    }

    private void InitializeApp()
    {
        // ── Vérification licence EN PREMIER — aucun service ni tray avant ça ──
        var license = LicenseService.LoadSaved();

        if (license.Tier == LicenseTier.Beta && LicenseService.IsBetaExpired())
        {
            System.Windows.MessageBox.Show(
                "Votre période de test de 30 jours est terminée.\n\nMerci d'avoir testé RAM-AI !",
                "RAM-AI — Période de test terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (license.Tier == LicenseTier.None)
        {
            // Fenêtre modale obligatoire — impossible d'y accéder sans clé valide.
            // LicenseWindow.OnClosing empêche la fermeture via le X sans licence.
            // Le bouton "Quitter" appelle Shutdown() directement.
            var licWin = new LicenseWindow();
            licWin.ShowDialog();

            // Si la licence est toujours absente après fermeture, on quitte.
            if (LicenseService.Current.Tier == LicenseTier.None)
            {
                Shutdown();
                return;
            }
        }

        // ── Licence validée : initialisation des services et du tray ──────────
        LogWatcher          = new LogWatcherService(LogPath);
        _foregroundWatcher  = new ForegroundWatcherService(SharedDir);
        MainVm              = new MainViewModel(LogWatcher, LicenseService);

        _tray = new NotifyIconService();
        _tray.OpenRequested         += ShowDashboard;
        _tray.QuitRequested         += ExitApp;
        _tray.StartServiceRequested += () => RunSc("start RamAI-Phase3");
        _tray.StopServiceRequested  += () => RunSc("stop RamAI-Phase3");

        // Différer l'ouverture de MainWindow à ApplicationIdle pour laisser WPF
        // terminer proprement la fermeture de LicenseWindow avant de créer MainWindow.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowDashboard));
    } // end InitializeApp

    // ── Gestionnaires d'exceptions globaux ───────────────────────────────────

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        ShowFatalError(args.Exception, "interface");
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception ex)
            ShowFatalError(ex, "thread");
    }

    private void ShowFatalError(Exception ex, string context)
    {
        System.Windows.MessageBox.Show(
            $"Erreur RAM-AI ({context}) :\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
            $"L'application va redémarrer.",
            "RAM-AI — Erreur critique",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = true }); }
        catch { }
        Shutdown(1);
    }

    // ── Nettoyage préventif JSON ──────────────────────────────────────────────

    /// <summary>
    /// Vérifie si le fichier JSON est valide. S'il existe et est corrompu
    /// (JSON invalide, tronqué…), il est supprimé pour garantir le démarrage.
    /// </summary>
    private static void SanitizeJsonFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                File.Delete(path);
                return;
            }
            // Vérification JSON rapide : tenter un parse sans déserialiser
            using var doc = System.Text.Json.JsonDocument.Parse(text);
        }
        catch
        {
            // Fichier non parseable → le supprimer
            try { File.Delete(path); } catch { }
        }
    }

    // ── Afficher / réafficher le dashboard ────────────────────────────────────
    // Toujours appelé via Dispatcher.Invoke pour gérer :
    //   • les appels différés depuis OnStartup (Dispatcher.BeginInvoke)
    //   • les appels depuis le thread tray (NotifyIconService)

    private void ShowDashboard()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null || !_mainWindow.IsLoaded)
                _mainWindow = new MainWindow();

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();
        }, DispatcherPriority.Normal);
    }

    // ── Bulle tray (appelée par MainWindow au premier close) ──────────────────

    internal static void ShowTrayBalloon(string title, string message) =>
        ((App)Current)._tray?.ShowBalloon(title, message);

    // ── Quitter proprement ────────────────────────────────────────────────────

    private void ExitApp()
    {
        MainVm?.SaveStats();
        _tray?.Dispose();
        _foregroundWatcher?.Dispose();
        LogWatcher.Dispose();
        Shutdown();
    }

    // ── Lancer sc.exe avec élévation UAC ─────────────────────────────────────

    private static void RunSc(string arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("sc.exe", arguments)
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                    WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
        }
        catch { /* UAC annulé par l'utilisateur */ }
    }
}
