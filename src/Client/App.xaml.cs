using System.Security.Cryptography;
using System.Windows;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

public partial class App : Application
{
    private HostSession? _workerSession;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        bool worker = e.Args.Any(a => a.Equals("--worker", StringComparison.OrdinalIgnoreCase));
        if (worker) StartWorkerMode();
        else new MainWindow().Show();
    }

    /// <summary>
    /// Headless unattended mode, launched by the Windows service into the active
    /// session. No window; reads machine config (server URL, fixed password, stable
    /// token) from %ProgramData%, keeps a HostSession alive, and reconnects forever.
    /// </summary>
    private void StartWorkerMode()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var cfg = AppConfig.LoadMachine("machine");
        bool dirty = false;
        if (string.IsNullOrEmpty(cfg.HostToken)) { cfg.HostToken = Guid.NewGuid().ToString("N"); dirty = true; }
        if (string.IsNullOrEmpty(cfg.UnattendedPassword)) { cfg.UnattendedPassword = GeneratePassword(); dirty = true; }
        if (dirty) cfg.SaveMachine("machine");

        _ = RunWorkerLoopAsync(cfg);
    }

    private async Task RunWorkerLoopAsync(AppConfig cfg)
    {
        while (true)
        {
            HostSession? session = null;
            try
            {
                session = new HostSession(cfg.ServerUrl, cfg.UnattendedPassword!, token: cfg.HostToken);
                _workerSession = session;
                session.IdAssigned += id =>
                {
                    // Persist the assigned ID so the attended UI can display it.
                    if (cfg.CurrentId != id) { cfg.CurrentId = id; cfg.SaveMachine("machine"); }
                };
                var down = new TaskCompletionSource();
                session.Status += s => { if (s.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase)) down.TrySetResult(); };
                await session.StartAsync();
                await down.Task;               // block until the connection drops
            }
            catch { /* fall through to retry */ }
            finally { try { session?.Dispose(); } catch { } }

            await Task.Delay(3000);            // reconnect backoff
        }
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var c = new char[8];
        for (int i = 0; i < c.Length; i++) c[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(c);
    }
}
