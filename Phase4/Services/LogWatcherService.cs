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

        // ── Diagnostics démarrage ─────────────────────────────────────────────
        Console.WriteLine("[Phase4] Surveillance : " + _logPath);
        Console.WriteLine("[Phase4] Fichier existe : " + File.Exists(_logPath));

        if (File.Exists(logPath))
        {
            long len = new FileInfo(logPath).Length;
            // Pré-charger les 100 dernières Ko
            _readPosition = Math.Max(0L, len - 100 * 1024L);
            Console.WriteLine($"[Phase4] Taille initiale : {len} octets | lecture depuis : {_readPosition}");
        }

        // dueTime = 0 : premier poll immédiat
        _timer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void Poll(object? _)
    {
        // ── 1. Vérifier existence du fichier ─────────────────────────────────
        if (!File.Exists(_logPath))
        {
            Console.WriteLine("[Phase4] ATTENTION : events.log introuvable → " + _logPath);
            return;
        }

        // ── 2. Log taille à chaque poll ───────────────────────────────────────
        long fileSize = new FileInfo(_logPath).Length;
        Console.WriteLine("[Phase4] Poll events.log - taille: " + fileSize + " | pos: " + _readPosition);

        try
        {
            using var fs = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Fichier tronqué / recyclé → reprendre depuis le début
            if (fs.Length < _readPosition) _readPosition = 0;

            // Pas de nouvelles données
            if (fs.Length == _readPosition) return;

            // Seek AVANT la création du StreamReader
            fs.Seek(_readPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, leaveOpen: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Ignorer les marqueurs (SERVICE_START, GAMING MODE ON, etc.)
                if (line.Contains("\"marker\"", StringComparison.Ordinal)) continue;

                // ── 3. Log de chaque ligne lue ────────────────────────────────
                Console.WriteLine("[Phase4] Ligne lue : " + line[..Math.Min(200, line.Length)]);

                try
                {
                    var entry = JsonSerializer.Deserialize<EventEntry>(line, JsonOpts);
                    if (entry is null || entry.Timestamp == default) continue;

                    // ── 4. Log spécifique selon le mode actif ─────────────────
                    if (entry.IsGamingMode)
                        Console.WriteLine("[Phase4] EVENT gaming reçu : IsGamingMode=True GameName='" + entry.GameName + "'");

                    if (entry.IsBrowserMode)
                        Console.WriteLine("[Phase4] Browser mode reçu : " + entry.BrowserName +
                                          " | tabs=" + entry.BrowserTabsOptimized +
                                          " | IsBrowserMode=" + entry.IsBrowserMode);

                    NewEntry?.Invoke(entry);
                }
                catch (JsonException jex)
                {
                    Console.WriteLine("[Phase4] ERREUR JSON : " + jex.Message);
                    Console.WriteLine("[Phase4] Ligne invalide : " + line[..Math.Min(120, line.Length)]);
                }
            }

            _readPosition = fs.Position;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Phase4] ERREUR lecture : " + ex.GetType().Name + " — " + ex.Message);
        }
    }

    public void Dispose() => _timer.Dispose();
}
