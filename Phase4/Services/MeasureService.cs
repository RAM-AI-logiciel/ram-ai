using System.IO;
using System.Text;
using System.Text.Json;

namespace RamAI.Phase4.Services;

/// <summary>
/// Un point de mesure enregistré toutes les 10 secondes.
/// </summary>
public sealed class MeasurePoint
{
    public DateTime Timestamp      { get; set; }
    public double   RamAvailableGb { get; set; }
    public double   RamUsedGb      { get; set; }
    public int      ProcessCount   { get; set; }
    public string   ActiveMode     { get; set; } = "Standard";
}

/// <summary>
/// Collecte les points de mesure en mémoire (max 1 000 entrées ≈ 2h46),
/// persiste dans measures.json à la demande, exporte en CSV.
/// Aucune exception ne remonte jamais vers l'appelant.
/// </summary>
public sealed class MeasureService
{
    private const int MaxPoints = 1000;

    private static readonly string SharedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAM-AI");

    public static readonly string MeasuresPath = Path.Combine(SharedDir, "measures.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    // Accès concurrentiel possible depuis le MeasureLoop (thread pool) → lock
    private readonly object           _lock   = new();
    private readonly List<MeasurePoint> _points = new();

    // ── Ajout d'un point ─────────────────────────────────────────────────────

    public void Add(MeasurePoint point)
    {
        lock (_lock)
        {
            _points.Add(point);
            // Rotation : ne garder que les MaxPoints derniers
            if (_points.Count > MaxPoints)
                _points.RemoveAt(0);
        }
    }

    // ── Lecture ───────────────────────────────────────────────────────────────

    /// <summary>Retourne une copie de la liste pour éviter les accès concurrents.</summary>
    public List<MeasurePoint> GetSnapshot()
    {
        lock (_lock)
            return new List<MeasurePoint>(_points);
    }

    public int Count { get { lock (_lock) return _points.Count; } }

    // ── Persistance JSON ──────────────────────────────────────────────────────

    /// <summary>Écrase measures.json avec les points actuels. Aucune exception.</summary>
    public void SaveToJson()
    {
        try
        {
            Directory.CreateDirectory(SharedDir);
            List<MeasurePoint> snapshot;
            lock (_lock) snapshot = new List<MeasurePoint>(_points);
            File.WriteAllText(MeasuresPath, JsonSerializer.Serialize(snapshot, Opts), Encoding.UTF8);
        }
        catch { }
    }

    // ── Export CSV ────────────────────────────────────────────────────────────

    /// <summary>
    /// Génère un fichier CSV adapté à Excel / tout tableur.
    /// Séparateur point-virgule, encodage UTF-8 BOM pour Excel.
    /// Retourne le chemin du fichier créé, ou null si erreur.
    /// </summary>
    public string? ExportCsv(string filePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            List<MeasurePoint> snapshot;
            lock (_lock) snapshot = new List<MeasurePoint>(_points);

            var sb = new StringBuilder();
            sb.AppendLine("Heure;RAM disponible (Go);RAM utilisée (Go);Processus actifs;Mode actif");
            foreach (var p in snapshot)
            {
                sb.AppendLine(
                    $"{p.Timestamp.ToLocalTime():HH:mm:ss};" +
                    $"{p.RamAvailableGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)};" +
                    $"{p.RamUsedGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)};" +
                    $"{p.ProcessCount};" +
                    $"{p.ActiveMode}");
            }

            // UTF-8 avec BOM pour que Excel l'ouvre directement sans problème d'encodage
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return filePath;
        }
        catch { return null; }
    }
}
