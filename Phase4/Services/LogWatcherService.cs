using System.IO;
using System.Text.Json;
using Timer = System.Threading.Timer;
using RamAI.Phase4.Models;

namespace RamAI.Phase4.Services;

/// <summary>
/// Surveille C:\ProgramData\RAM-AI\events.log et émet
/// <see cref="NewEntry"/> pour chaque ligne JSON valide ajoutée.
///
/// Phase3 (service) écrit dans ce fichier.
/// Phase4 (dashboard) le lit toutes les 2 s via un timer.
/// </summary>
public sealed class LogWatcherService : IDisposable
{
    private readonly string _logPath;
    private readonly Timer  _timer;
    private long            _readPosition;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Déclenché pour chaque entrée de tick JSON valide.</summary>
    public event Action<EventEntry>? NewEntry;

    public LogWatcherService(string logPath)
    {
        _logPath = logPath;

        if (File.Exists(logPath))
        {
            long len = new FileInfo(logPath).Length;
            // Démarrer depuis les 100 dernières Ko pour afficher l'historique récent
            _readPosition = Math.Max(0L, len - 100 * 1024L);
        }

        // dueTime = 0 : premier poll immédiat
        _timer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void Poll(object? _)
    {
        if (!File.Exists(_logPath)) return;

        try
        {
            using var fs = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Fichier tronqué / recyclé → reprendre depuis le début
            if (fs.Length < _readPosition) _readPosition = 0;

            if (fs.Length == _readPosition) return;

            fs.Seek(_readPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, leaveOpen: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("\"marker\"", StringComparison.Ordinal)) continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<EventEntry>(line, JsonOpts);
                    if (entry is null || entry.Timestamp == default) continue;
                    NewEntry?.Invoke(entry);
                }
                catch (JsonException) { /* ligne corrompue — ignorer */ }
            }

            _readPosition = fs.Position;
        }
        catch { /* fichier verrouillé ou supprimé entre le test et l'ouverture */ }
    }

    public void Dispose() => _timer.Dispose();
}
