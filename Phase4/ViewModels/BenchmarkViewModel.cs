using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using RamAI.Phase4.Models;
using RamAI.Phase4.Services;
using SkiaSharp;
using Application    = System.Windows.Application;
using MessageBox     = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace RamAI.Phase4.ViewModels;

public sealed partial class BenchmarkViewModel : ObservableObject
{
    private readonly BenchmarkService  _svc;
    private readonly LogWatcherService _log;

    // ── Statut ────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchBenchmarkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelBenchmarkCommand))]
    private bool _isRunning;

    [ObservableProperty] private int    _progressValue;
    [ObservableProperty] private string _statusText    = "Cliquez sur « Lancer benchmark » pour démarrer une mesure de 60 s.";
    [ObservableProperty] private int    _countdown;

    // ── Métriques session courante ────────────────────────────────────────────
    [ObservableProperty] private string _ramTotalText    = "—";
    [ObservableProperty] private string _ramBeforeText   = "—";
    [ObservableProperty] private string _ramFreedText    = "0,00 Go";
    [ObservableProperty] private string _processesText   = "0";
    [ObservableProperty] private string _avgCycleText    = "—";
    [ObservableProperty] private string _activeModeText  = "—";

    // ── RAM libérée par mode ──────────────────────────────────────────────────
    [ObservableProperty] private string _gamingFreedText = "—";
    [ObservableProperty] private string _turboFreedText  = "—";
    [ObservableProperty] private string _iaFreedText     = "—";
    [ObservableProperty] private string _ecoFreedText    = "—";
    [ObservableProperty] private string _navFreedText    = "—";

    // ── Historique ────────────────────────────────────────────────────────────
    public ObservableCollection<BenchmarkSession> Sessions { get; } = new();

    // ── LiveCharts ────────────────────────────────────────────────────────────
    [ObservableProperty] private ISeries[] _series = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[]    _xAxes  = new[] { new Axis() };
    [ObservableProperty] private Axis[]    _yAxes  = new[] { new Axis() };

    // ── Accumulateurs session ────────────────────────────────────────────────
    private long   _accMbFreed;
    private int    _accProcesses;
    private long   _accLatency;
    private long   _accCycles;
    private string _accMode = "Standard";
    private readonly Dictionary<string, double> _accModeGb = new();

    private CancellationTokenSource? _cts;
    private const int DurationSeconds = 60;

    public BenchmarkViewModel(BenchmarkService svc, LogWatcherService log)
    {
        _svc = svc;
        _log = log;
        LoadAndRefresh();
    }

    // ── Chargement + graphique ────────────────────────────────────────────────

    private void LoadAndRefresh()
    {
        Sessions.Clear();
        foreach (var s in _svc.LoadHistory().OrderByDescending(s => s.StartTime))
            Sessions.Add(s);
        RefreshChart();
    }

    private void RefreshChart()
    {
        var recent = Sessions.Reverse().TakeLast(10).ToList();

        if (recent.Count == 0)
        {
            Series = Array.Empty<ISeries>();
            return;
        }

        Series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values         = recent.Select(s => s.TotalRamFreedGb).ToList(),
                Name           = "RAM libérée (Go)",
                Stroke         = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 2 },
                GeometryStroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 2 },
                GeometryFill   = new SolidColorPaint(SKColors.LimeGreen),
                Fill           = new SolidColorPaint(new SKColor(63, 185, 80, 35)),
                GeometrySize   = 8,
                LineSmoothness = 0.4,
            }
        };

        XAxes = new[]
        {
            new Axis
            {
                Labels          = recent.Select(s => s.StartTime.ToString("dd/MM HH:mm")).ToList(),
                LabelsPaint     = new SolidColorPaint(new SKColor(139, 148, 158)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(48, 54, 61)),
                TextSize        = 10,
                LabelsRotation  = -25,
            }
        };

        YAxes = new[]
        {
            new Axis
            {
                Name            = "Go libérés",
                NamePaint       = new SolidColorPaint(new SKColor(139, 148, 158)),
                LabelsPaint     = new SolidColorPaint(new SKColor(139, 148, 158)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(48, 54, 61)),
                TextSize        = 10,
                Labeler         = v => $"{v:F1}",
                MinLimit        = 0,
            }
        };
    }

    // ── Commandes ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchBenchmarkAsync()
    {
        IsRunning     = true;
        ProgressValue = 0;
        Countdown     = DurationSeconds;

        _accMbFreed = 0; _accProcesses = 0;
        _accLatency = 0; _accCycles    = 0;
        _accMode    = "Standard";
        _accModeGb.Clear();

        var (totalMb, usedBefore) = SystemMemory.GetPhysicalMemoryMb();
        var startTime = DateTime.Now;

        RamTotalText   = $"{totalMb / 1024.0:F1} Go";
        RamBeforeText  = $"{usedBefore / 1024.0:F2} Go";
        RamFreedText   = "0,00 Go";
        ProcessesText  = "0";
        AvgCycleText   = "—";
        ActiveModeText = "Standard";
        StatusText     = $"Mesure en cours… {DurationSeconds}s";

        _cts = new CancellationTokenSource();
        _log.NewEntry += AccumulateEntry;

        try
        {
            for (int t = 1; t <= DurationSeconds; t++)
            {
                await Task.Delay(1000, _cts.Token);
                Countdown     = DurationSeconds - t;
                ProgressValue = t * 100 / DurationSeconds;
                StatusText    = Countdown > 0
                    ? $"Mesure en cours… {Countdown}s restantes"
                    : "Finalisation…";
                Application.Current?.Dispatcher.Invoke(UpdateLiveMetrics);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Benchmark annulé.";
        }
        finally
        {
            _log.NewEntry -= AccumulateEntry;
        }

        if (!_cts.IsCancellationRequested)
            FinalizeSession(startTime, totalMb, usedBefore);

        _cts      = null;
        IsRunning = false;
    }

    private bool CanLaunch() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelBenchmark() => _cts?.Cancel();

    private bool CanCancel() => IsRunning;

    [RelayCommand]
    private void ExportReport()
    {
        if (Sessions.Count == 0)
        {
            MessageBox.Show("Aucune session à exporter.", "RAM-AI",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title      = "Exporter le rapport RAM-AI",
            FileName   = $"RAM-AI_Benchmark_{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".csv",
            Filter     = "CSV (*.csv)|*.csv|Texte (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _svc.ExportCsv(Sessions, dlg.FileName);
            MessageBox.Show($"Rapport exporté :\n{dlg.FileName}",
                "RAM-AI — Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur export : {ex.Message}", "RAM-AI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Collecte pendant la mesure ────────────────────────────────────────────

    private void AccumulateEntry(EventEntry e)
    {
        if (e.PhysicalMbFreed >= 1) _accMbFreed += e.PhysicalMbFreed;
        _accProcesses += e.ColdEvicted + e.AiProcessesOptimized;
        _accLatency   += e.LatencyMs;
        _accCycles++;

        string mode;
        if      (e.IsGamingMode)     mode = "Gaming";
        else if (e.IsAiMode)         mode = "IA";
        else if (e.IsBrowserMode)    mode = "Navigateur";
        else if (e.IsEcoMode)        mode = "Éco";
        else if (e.IsTournamentMode) mode = "Turbo";
        else                         mode = "Standard";

        _accMode = mode;
        if (e.PhysicalMbFreed >= 1)
        {
            double gb = e.PhysicalMbFreed / 1024.0;
            _accModeGb[mode] = _accModeGb.TryGetValue(mode, out var cur) ? cur + gb : gb;
        }
    }

    private void UpdateLiveMetrics()
    {
        RamFreedText   = $"{_accMbFreed / 1024.0:F2} Go";
        ProcessesText  = $"{_accProcesses:N0}";
        ActiveModeText = _accMode;
        AvgCycleText   = _accCycles > 0 ? $"{_accLatency / _accCycles:F0} ms" : "—";
    }

    private void FinalizeSession(DateTime start, long totalMb, long usedBefore)
    {
        var session = new BenchmarkSession
        {
            StartTime          = start,
            EndTime            = DateTime.Now,
            TotalRamGb         = totalMb / 1024.0,
            RamUsedBeforeGb    = usedBefore / 1024.0,
            TotalRamFreedGb    = _accMbFreed / 1024.0,
            ProcessesOptimized = _accProcesses,
            AvgCycleMs         = _accCycles > 0 ? _accLatency / (double)_accCycles : 0,
            ActiveMode         = _accMode,
            RamFreedByMode     = new Dictionary<string, double>(_accModeGb),
        };

        _svc.SaveSession(session);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            Sessions.Insert(0, session);
            RefreshChart();
            UpdateLiveMetrics();
            GamingFreedText = ModeGb("Gaming");
            TurboFreedText  = ModeGb("Turbo");
            IaFreedText     = ModeGb("IA");
            EcoFreedText    = ModeGb("Éco");
            NavFreedText    = ModeGb("Navigateur");
            StatusText = $"✓ Terminé — {session.TotalRamFreedGb:F2} Go récupérés, " +
                         $"{session.ProcessesOptimized:N0} processus en {session.DurationLabel}";
            ProgressValue = 100;
        });
    }

    private string ModeGb(string mode) =>
        _accModeGb.TryGetValue(mode, out var v) ? $"{v:F2} Go" : "—";
}
