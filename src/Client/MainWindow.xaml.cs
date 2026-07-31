using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private HostSession? _session;
    private bool _running;

    private readonly Updater _updater = new("client", elevate: true);
    private UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("client");
        ServerBox.Text = _config.ServerUrl;
        PwBox.Text = _config.FixedPassword ?? GeneratePassword();
        ShowUnattendedInfo();
        Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0) return;
        var info = await _updater.CheckAsync(url);
        if (info == null) return;
        _pendingUpdate = info;
        UpdateBtn.Content = $"⬇ Update to v{info.Version}";
        UpdateBtn.ToolTip = string.IsNullOrWhiteSpace(info.Notes) ? null : info.Notes;
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } info) return;
        if (_running)
        {
            SetStatus("Stop sharing before updating.");
            return;
        }
        UpdateBtn.IsEnabled = false;
        try
        {
            SetStatus($"Downloading v{info.Version}…");
            var progress = new Progress<double>(p => Dispatcher.Invoke(() => SetStatus($"Downloading v{info.Version}… {(int)(p * 100)}%")));
            var stage = await _updater.DownloadAsync(ServerBox.Text.Trim(), info, progress);
            SetStatus("Installing — the app will restart…");
            _updater.LaunchAndExit(stage);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus("Update failed: " + ex.Message);
            UpdateBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// If the unattended service is installed it self-provisions a fixed password (and,
    /// once connected, a stable ID) into machine config under %ProgramData% — which is
    /// world-readable, so this attended app can surface those credentials to the operator.
    /// Hidden entirely when unattended access isn't set up.
    /// </summary>
    private void ShowUnattendedInfo()
    {
        var machine = AppConfig.LoadMachine("machine");
        if (string.IsNullOrEmpty(machine.UnattendedPassword)) return;   // not provisioned

        UnPwBox.Text = machine.UnattendedPassword;
        UnattendedPanel.Visibility = Visibility.Visible;

        // The worker writes CurrentId once it connects (async, in another process), so poll
        // until the ID resolves, then stop.
        if (string.IsNullOrEmpty(machine.CurrentId))
        {
            UnIdBox.Text = "waiting…";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                var id = AppConfig.LoadMachine("machine").CurrentId;
                if (string.IsNullOrEmpty(id)) return;
                UnIdBox.Text = FormatId(id);
                timer.Stop();
            };
            timer.Start();
        }
        else UnIdBox.Text = FormatId(machine.CurrentId);
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { await StopAsync(); return; }

        _config.ServerUrl = ServerBox.Text.Trim();
        _config.Save("client");

        var pw = PwBox.Text.Trim();
        if (pw.Length == 0) { SetStatus("Set a password first."); return; }

        _session = new HostSession(_config.ServerUrl, pw);
        _session.IdAssigned += id => Dispatcher.Invoke(() => IdBox.Text = FormatId(id));
        _session.Status += s => Dispatcher.Invoke(() => SetStatus(s));
        _session.SessionActive += active => Dispatcher.Invoke(() => ShowBanner(active));
        _session.Files.ReceiveStarted += (n, _) => Dispatcher.Invoke(() => SetStatus($"Receiving {n}…"));
        _session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() => SetStatus($"Received {n} → {path}"));
        _session.Files.Error += msg => Dispatcher.Invoke(() => SetStatus("File transfer failed: " + msg));

        try
        {
            SetControlsRunning(true);
            await _session.StartAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Failed to start: " + ex.Message);
            SetControlsRunning(false);
            _session?.Dispose();
            _session = null;
        }
    }

    private async Task StopAsync()
    {
        if (_session != null) { await _session.StopAsync(); _session.Dispose(); _session = null; }
        SetControlsRunning(false);
        IdBox.Text = "—";
        ShowBanner(false);
        SetStatus("Stopped");
    }

    private void SetControlsRunning(bool running)
    {
        _running = running;
        StartBtn.Content = running ? "Stop" : "Start";
        StartBtn.Background = new SolidColorBrush(running ? Color.FromRgb(0xda, 0x36, 0x33) : Color.FromRgb(0x23, 0x86, 0x36));
        ServerBox.IsEnabled = !running;
        PwBox.IsEnabled = !running;
    }

    private void ShowBanner(bool active)
        => Banner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    private void SetStatus(string s) => StatusText.Text = s;

    private static string FormatId(string id)
        => id.Length == 9 ? $"{id[..3]} {id.Substring(3, 3)} {id[6..]}" : id;

    /// <summary>6-char human-friendly password (no ambiguous chars).</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no l/o/0/1
        var chars = new char[6];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_session != null) await _session.StopAsync();
        base.OnClosed(e);
    }
}
