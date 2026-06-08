using System.Drawing;
using System.IO;

namespace RamAI.Phase4.Services;

/// <summary>
/// Icône dans la barre système (zone de notification Windows).
/// Charge RAM-AI.ico depuis le dossier Assets\ de l'exe.
/// </summary>
public sealed class NotifyIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon        _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _startItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _stopItem;

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? StartServiceRequested;
    public event Action? StopServiceRequested;

    public NotifyIconService()
    {
        _startItem = new System.Windows.Forms.ToolStripMenuItem(
            "Démarrer le service", null, (_, _) => StartServiceRequested?.Invoke());
        _stopItem  = new System.Windows.Forms.ToolStripMenuItem(
            "Arrêter le service",  null, (_, _) => StopServiceRequested?.Invoke());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir",  null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => QuitRequested?.Invoke());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon             = LoadIcon(),
            Text             = "RAM-AI — En cours d'optimisation",
            ContextMenuStrip = menu,
            Visible          = true,
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>
    /// Charge RAM-AI.ico depuis la ressource WPF embarquée dans l'assembly.
    /// Fonctionne en dev ET après dotnet publish (pas de fichier physique requis).
    /// Repli 1 : fichier Assets\RAM-AI.ico à côté de l'exe (dev sans pack URI).
    /// Repli 2 : icône système générique.
    /// </summary>
    private static Icon LoadIcon()
    {
        // Priorité : ressource WPF embarquée (pack URI) — disponible en publish
        try
        {
            var uri  = new Uri("pack://application:,,,/Assets/RAM-AI.ico");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is not null)
                return new Icon(info.Stream);
        }
        catch { /* pack URI indisponible hors contexte WPF — essayer le fichier */ }

        // Repli : fichier physique (utile en dev si WPF non encore initialisé)
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RAM-AI.ico");
        if (File.Exists(iconPath))
        {
            try { return new Icon(iconPath); }
            catch { }
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// Affiche une bulle d'information dans la zone de notification.
    /// </summary>
    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(4000, title, message,
                             System.Windows.Forms.ToolTipIcon.Info);

    public void SetServiceRunning(bool running)
    {
        _startItem.Enabled = !running;
        _stopItem.Enabled  =  running;
    }

    public void Dispose() => _icon.Dispose();
}
