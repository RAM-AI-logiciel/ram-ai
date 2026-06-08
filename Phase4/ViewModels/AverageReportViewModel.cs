using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;

namespace RamAI.Phase4.ViewModels;

public sealed partial class AverageReportViewModel : ObservableObject
{
    // ── Statistiques affichées ────────────────────────────────────────────────
    [ObservableProperty] private int    _sessionCount;
    [ObservableProperty] private string _avgDurationText        = "—";
    [ObservableProperty] private string _avgRamFreedText        = "—";
    [ObservableProperty] private string _avgRamFreedPerHourText = "—";
    [ObservableProperty] private string _avgProcessesText       = "—";
    [ObservableProperty] private string _topModeText            = "—";
    [ObservableProperty] private string _totalRamFreedText      = "—";
    [ObservableProperty] private string _totalProcessesText     = "—";
    [ObservableProperty] private string _noDataVisibility       = "Visible";
    [ObservableProperty] private string _dataVisibility         = "Collapsed";

    private readonly List<SessionRecord> _sessions;

    public AverageReportViewModel(SessionHistoryService svc)
    {
        _sessions = svc.Load();
        Compute();
    }

    private void Compute()
    {
        SessionCount = _sessions.Count;
        if (_sessions.Count == 0) return;

        NoDataVisibility = "Collapsed";
        DataVisibility   = "Visible";

        double totalHours      = _sessions.Sum(s => s.Duration.TotalHours);
        double avgDurSec       = _sessions.Average(s => s.DurationSeconds);
        double avgRamGb        = _sessions.Average(s => s.RamFreedGb);
        double totalRamGb      = _sessions.Sum(s => s.RamFreedGb);
        double avgRamPerHour   = totalHours > 0 ? totalRamGb / totalHours : 0;
        double avgProcs        = _sessions.Average(s => s.ProcessesOptimized);
        long   totalProcs      = _sessions.Sum(s => s.ProcessesOptimized);

        var avgDur = TimeSpan.FromSeconds(avgDurSec);
        AvgDurationText        = $"{(int)avgDur.TotalHours:D2}h {avgDur.Minutes:D2}m {avgDur.Seconds:D2}s";
        AvgRamFreedText        = $"{avgRamGb:F2} Go";
        AvgRamFreedPerHourText = $"{avgRamPerHour:F2} Go/h";
        AvgProcessesText       = $"{avgProcs:F0}";
        TotalRamFreedText      = $"{totalRamGb:F2} Go";
        TotalProcessesText     = $"{totalProcs:N0}";

        // Mode le plus utilisé
        TopModeText = _sessions
            .GroupBy(s => s.ActiveMode)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ({g.Count()} session{(g.Count() > 1 ? "s" : "")})")
            .FirstOrDefault() ?? "—";
    }

    [RelayCommand]
    private void ExportReport()
    {
        var dlg = new SaveFileDialog
        {
            Title      = "Exporter le rapport moyen RAM-AI",
            FileName   = $"RAM-AI_Rapport-Moyen_{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".txt",
            Filter     = "Texte (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, BuildReportText(), Encoding.UTF8);
            MessageBox.Show($"Rapport exporté :\n{dlg.FileName}",
                "RAM-AI", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur export : {ex.Message}", "RAM-AI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public string BuildReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════╗");
        sb.AppendLine("║           RAM-AI — Rapport moyen des sessions         ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"Généré le               : {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"Nombre total de sessions : {SessionCount}");
        sb.AppendLine();
        sb.AppendLine("── Moyennes par session ────────────────────────────────");
        sb.AppendLine($"Durée moyenne           : {AvgDurationText}");
        sb.AppendLine($"RAM récupérée (moy.)    : {AvgRamFreedText}");
        sb.AppendLine($"RAM récupérée / heure   : {AvgRamFreedPerHourText}");
        sb.AppendLine($"Processus opt. (moy.)   : {AvgProcessesText}");
        sb.AppendLine($"Mode le plus utilisé    : {TopModeText}");
        sb.AppendLine();
        sb.AppendLine("── Totaux cumulés ──────────────────────────────────────");
        sb.AppendLine($"RAM récupérée (total)   : {TotalRamFreedText}");
        sb.AppendLine($"Processus opt. (total)  : {TotalProcessesText}");
        sb.AppendLine();
        sb.AppendLine("────────────────────────────────────────────────────────");
        sb.AppendLine($"Généré par RAM-AI v1.0 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return sb.ToString();
    }
}
