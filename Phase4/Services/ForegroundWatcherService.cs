using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace RamAI.Phase4.Services;

/// <summary>
/// Écrit C:\ProgramData\RAM-AI\foreground.json toutes les 500 ms.
/// Phase3 (Session 0, Windows Service) lit ce fichier pour connaître la fenêtre
/// au premier plan — GetForegroundWindow() depuis Session 0 retourne toujours
/// IntPtr.Zero (isolement Session 0 depuis Windows Vista).
///
/// Ce type est dans la liste SkipType d'Obfuscar (obfuscar.xml) pour préserver
/// les noms des méthodes P/Invoke (DllImport sans EntryPoint explicite).
/// </summary>
internal sealed class ForegroundWatcherService : IDisposable
{
    // EntryPoint explicite sur chaque P/Invoke : Obfuscar peut renommer la méthode
    // C# même quand elle est extern ; l'EntryPoint garantit que le nom DLL reste correct.
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SM_CXSCREEN     = 0;
    private const int SM_CYSCREEN     = 1;
    private const int EdgeTolerancePx = 8;

    // Fichier de log d'erreur dans %TEMP% — toujours accessible en écriture, même si
    // C:\ProgramData\RAM-AI n'est pas encore créé ou si l'écriture principale échoue.
    private static readonly string ErrorLogPath =
        Path.Combine(Path.GetTempPath(), "ram_ai_fg_error.txt");

    private readonly string _path;
    private readonly string _sharedDir;
    private readonly Timer  _timer;

    internal ForegroundWatcherService(string sharedDir)
    {
        _sharedDir = sharedDir;
        _path      = Path.Combine(sharedDir, "foreground.json");
        // Créer le répertoire immédiatement — ne pas attendre la 1ère écriture.
        // MainViewModel crée aussi le répertoire, mais ForegroundWatcher peut
        // tirer avant lui sur le thread pool (TimeSpan.Zero = premier tick immédiat).
        try { Directory.CreateDirectory(sharedDir); } catch { }
        _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    private void Update(object? _)
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            int    pid  = -1;
            string name = string.Empty;
            bool   isFs = false;
            int    winW = 0, winH = 0, scrW = 0, scrH = 0;

            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint uid);
                pid = (int)uid;

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    name = proc.ProcessName;
                }
                catch { /* processus système (pid=4) ou disparu entre les deux appels */ }

                // Calcul fullscreen : dimensions fenêtre vs écran principal
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    scrW = GetSystemMetrics(SM_CXSCREEN);
                    scrH = GetSystemMetrics(SM_CYSCREEN);
                    winW = rect.Right  - rect.Left;
                    winH = rect.Bottom - rect.Top;
                    isFs = rect.Left   <= EdgeTolerancePx
                        && rect.Top    <= EdgeTolerancePx
                        && rect.Right  >= scrW - EdgeTolerancePx
                        && rect.Bottom >= scrH - EdgeTolerancePx;
                }
            }

            var json = JsonSerializer.Serialize(new
            {
                pid,
                name,
                isFullscreen = isFs,
                winW,
                winH,
                scrW,
                scrH,
                timestamp = DateTime.UtcNow.ToString("O"),
            });

            // Écriture atomique : écrire dans un temp puis renommer pour éviter
            // qu'un lecteur (Phase3) lise un JSON partiellement écrit.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Rendre l'erreur visible dans %TEMP%\ram_ai_fg_error.txt pour diagnostic.
            // Ne jamais propager l'exception — le timer doit continuer à tourner.
            try
            {
                File.AppendAllText(
                    ErrorLogPath,
                    $"{DateTime.UtcNow:O} ForegroundWatcher.Update: {ex.GetType().Name}: {ex.Message}\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    public void Dispose() => _timer.Dispose();
}
