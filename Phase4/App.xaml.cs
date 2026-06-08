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

    private NotifyIconService? _tray;
    private MainWindow?        _mainWindow;

    // events.log dans C:\ProgramData\RAM-AI\ — même dossier que les fichiers flag.
    // Phase3 écrit ici (RamAiService.cs), Phase4 lit ici.
    // Fonctionne en dev ET en production sans chemin hardcodé.
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAM-AI", "events.log");

    // ── Démarrage ─────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // L'application vit dans la barre système même si la fenêtre est fermée
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
        LogWatcher = new LogWatcherService(LogPath);
        MainVm     = new MainViewModel(LogWatcher, LicenseService);

        _tray = new NotifyIconService();
        _tray.OpenRequested         += ShowDashboard;
        _tray.QuitRequested         += ExitApp;
        _tray.StartServiceRequested += () => RunSc("start RamAI-Phase3");
        _tray.StopServiceRequested  += () => RunSc("stop RamAI-Phase3");

        // Différer l'ouverture de MainWindow à ApplicationIdle pour laisser WPF
        // terminer proprement la fermeture de LicenseWindow avant de créer MainWindow.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowDashboard));
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
        _tray?.Dispose();
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
