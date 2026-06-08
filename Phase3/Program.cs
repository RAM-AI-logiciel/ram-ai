using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RamAI.Phase3.Service;

// ── Self-install / uninstall ──────────────────────────────────────────────────
//
//  Usage:
//    RamAI.Phase3.exe --install      register as Windows Service then start
//    RamAI.Phase3.exe --uninstall    stop + delete the service
//    RamAI.Phase3.exe                run (either as service or console)
//
// ─────────────────────────────────────────────────────────────────────────────

const string ServiceName    = "RamAI-Phase3";
const string ServiceDisplay = "RAM-AI Phase 3 — Memory Predictor";
const string ServiceDesc    = "Evicts cold-process pages and prefetches hot-process pages " +
                              "using an ML.NET model trained on memory-access patterns.";

if (args.Contains("--install"))
{
    Install();
    return;
}
if (args.Contains("--uninstall"))
{
    Uninstall();
    return;
}

// ── Host ─────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// Wire up Windows SCM  (no-op when running as a console app)
builder.Services.AddWindowsService(o =>
{
    o.ServiceName = ServiceName;
});

builder.Services.AddHostedService<RamAiService>();

// Optional: override paths via appsettings.json or env vars
//   RamAi__ModelPath  RamAi__CachePath  RamAi__LogPath

await builder.Build().RunAsync();
return;

// ── Install / Uninstall helpers ───────────────────────────────────────────────

static void Install()
{
    string exe = Environment.ProcessPath
                 ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    exe = Path.GetFullPath(exe);

    Console.WriteLine($"Installing service '{ServiceName}' …");

    RunSc($"create \"{ServiceName}\" " +
          $"binPath= \"{exe}\" " +
          $"DisplayName= \"{ServiceDisplay}\" " +
          $"start= auto");

    RunSc($"description \"{ServiceName}\" \"{ServiceDesc}\"");

    // Recovery: restart after 1st and 2nd failure; no action on 3rd+
    RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/5000/restart/10000//");

    Console.WriteLine("Starting service …");
    RunSc($"start \"{ServiceName}\"");
    Console.WriteLine("Done. Check 'sc query RamAI-Phase3' to verify.");
}

static void Uninstall()
{
    Console.WriteLine($"Stopping service '{ServiceName}' …");
    RunSc($"stop \"{ServiceName}\"", ignoreErrors: true);
    System.Threading.Thread.Sleep(2000);

    Console.WriteLine("Deleting service …");
    RunSc($"delete \"{ServiceName}\"");
    Console.WriteLine("Done.");
}

static void RunSc(string arguments, bool ignoreErrors = false)
{
    var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
    };

    using var proc = System.Diagnostics.Process.Start(psi)!;
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());

    if (!ignoreErrors && proc.ExitCode != 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"sc.exe exited with code {proc.ExitCode}");
        Console.ResetColor();
        Environment.Exit(proc.ExitCode);
    }
}
