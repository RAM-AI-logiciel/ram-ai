using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace RamAI.Phase4.Services;

/// <summary>
/// Écrit C:\ProgramData\RAM-AI\foreground.json toutes les 500 ms.
/// Phase3 (Session 0, Windows Service) lit ce fichier pour connaître la fenêtre
/// au premier plan — GetForegroundWindow() depuis Session 0 retourne toujours
/// IntPtr.Zero (isolement Session 0 depuis Windows Vista).
/// </summary>
internal sealed class ForegroundWatcherService : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint  GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool  GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int   GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SM_CXSCREEN     = 0;
    private const int SM_CYSCREEN     = 1;
    private const int EdgeTolerancePx = 8;

    private readonly string _path;
    private readonly Timer  _timer;

    internal ForegroundWatcherService(string sharedDir)
    {
        _path  = Path.Combine(sharedDir, "foreground.json");
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
                timestamp    = DateTime.UtcNow.ToString("O"),
            });

            File.WriteAllText(_path, json, System.Text.Encoding.UTF8);
        }
        catch { /* ne jamais planter le timer pour un tick raté */ }
    }

    public void Dispose() => _timer.Dispose();
}
