using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace RemoteDesktop.Master;

/// <summary>
/// App startup: registers the <c>hangar://</c> URL scheme (so the web dashboard's Connect
/// button can launch us), enforces a single instance, and routes a
/// <c>hangar://connect?...</c> URI to the running window to open a session.
/// </summary>
public partial class App : Application
{
    private const string Scheme = "hangar";
    private const string PipeName = "FtdRemoteControl.connect";
    private static Mutex? _mutex;
    private MainWindow? _main;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // If launched loose (e.g. from Downloads), install into a proper per-user location and
        // relaunch from there — then this instance exits.
        if (SelfInstall.RelocateIfLoose(e.Args)) { Shutdown(); return; }

        RegisterUriScheme();
        var uri = e.Args.FirstOrDefault(a => a.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, @"Local\FtdRemoteControlSingleton", out bool isNew);
        if (!isNew)
        {
            // Already running — hand the connect URI to the primary instance and exit.
            if (uri != null) SendUri(uri);
            Shutdown();
            return;
        }

        StartPipeServer();
        _main = new MainWindow();
        _main.Show();
        if (uri != null) _main.HandleConnectUri(uri);
    }

    private static void RegisterUriScheme()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            k.SetValue("", "URL:Hangar Remote");
            k.SetValue("URL Protocol", "");
            using var cmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme + @"\shell\open\command");
            cmd.SetValue("", "\"" + exe + "\" \"%1\"");
        }
        catch { /* non-fatal: scheme just won't be registered */ }
    }

    private void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    s.WaitForConnection();
                    using var r = new StreamReader(s);
                    var uri = r.ReadLine();
                    if (!string.IsNullOrEmpty(uri))
                        Dispatcher.Invoke(() => { _main?.Activate(); _main?.HandleConnectUri(uri); });
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true };
        t.Start();
    }

    private static void SendUri(string uri)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(2000);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(uri);
        }
        catch { }
    }
}
