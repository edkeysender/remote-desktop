using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service;

/// <summary>
/// The AllViewer service body. Keeps exactly one capture worker
/// (AllViewerAgent.exe --worker) alive in the ACTIVE console session, running as
/// LocalSystem, on the CURRENT input desktop. Respawns it when:
///   • the active session changes (user logs on / fast-user-switch), or
///   • the input desktop changes (Default ↔ Winlogon — i.e. a UAC prompt appears or
///     the machine locks/unlocks), so capture + input follow onto the secure desktop, or
///   • the worker process exits.
///
/// Running the worker as SYSTEM on the secure desktop is what makes UAC prompts and
/// the lock screen visible and controllable — a normal user process cannot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Supervisor : BackgroundService
{
    private readonly ILogger<Supervisor> _log;
    private readonly string _workerExe;
    private uint _pid;
    private uint _session = INVALID;
    private string _desktop = "";
    private const uint INVALID = 0xFFFFFFFF;

    public Supervisor(ILogger<Supervisor> log)
    {
        _log = log;
        _workerExe = Path.Combine(AppContext.BaseDirectory, "AllViewerAgent.exe");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _log.LogInformation("AllViewer service starting. Worker: {Worker}", _workerExe);
        if (!File.Exists(_workerExe))
            _log.LogError("Worker exe not found next to the service. The capture worker will not start.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                Tick();
        }
        catch (OperationCanceledException) { }
        finally { KillWorker(); }
    }

    private void Tick()
    {
        uint session = SessionLauncher.ActiveSessionId;
        if (session == INVALID)
        {
            // No one at the console (rare). Nothing to capture; drop any worker.
            if (_pid != 0) { KillWorker(); _session = INVALID; _desktop = ""; }
            return;
        }

        string desktop = SessionLauncher.CurrentInputDesktop();
        bool alive = _pid != 0 && IsAlive(_pid);

        if (alive && session == _session && desktop == _desktop) return;   // steady state

        if (_pid != 0) KillWorker();
        try
        {
            _pid = SessionLauncher.LaunchInSession(session, _workerExe, "--worker", desktop);
            _session = session; _desktop = desktop;
            _log.LogInformation("Launched worker pid={Pid} in session {Session} on desktop {Desktop}", _pid, session, desktop);
        }
        catch (Exception ex)
        {
            _pid = 0;
            _log.LogError(ex, "Failed to launch worker in session {Session} on desktop {Desktop}", session, desktop);
        }
    }

    private static bool IsAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
        catch { return false; }
    }

    private void KillWorker()
    {
        if (_pid == 0) return;
        try { using var p = Process.GetProcessById((int)_pid); p.Kill(); }
        catch { }
        _pid = 0;
    }
}
