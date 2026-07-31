using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using RemoteDesktop.Client;   // HostSession (host engine, reused)
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// The unified app hub. Hosts this PC (always on while open) and lets the signed-in user
/// browse their org's computers and open each session in its own <see cref="ViewerWindow"/>.
/// Signing in claims this PC into the org and enables password-less connect by membership.
/// </summary>
public partial class MainWindow : Window
{
    private sealed record CompRow(string? RelayId, string Display, string GroupName, bool Online, string StatusText);

    private readonly AppConfig _config;
    private readonly AccountClient _account = new();
    private AccountSession? _acct;

    // always-on host loop
    private HostSession? _host;
    private CancellationTokenSource? _hostCts;
    private string _appliedServer = "", _appliedPw = "", _appliedAuth = "";

    private readonly Updater _updater = new("app", elevate: false);
    private UpdateInfo? _pendingUpdate;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("app");
        if (string.IsNullOrEmpty(_config.HostToken)) _config.HostToken = Guid.NewGuid().ToString("N");
        if (string.IsNullOrEmpty(_config.FixedPassword)) _config.FixedPassword = GeneratePassword();
        _config.Save("app");

        ServerBox.Text = _config.ServerUrl;
        HostPwBox.Text = _config.FixedPassword;
        EmailBox.Text = _config.AccountEmail ?? "";

        Loaded += async (_, _) =>
        {
            StartUpdateChecks();
            await RestoreSessionAsync();   // validate a saved token, then start hosting
        };
    }

    // ------------------------------- account -------------------------------

    private async Task RestoreSessionAsync()
    {
        if (!string.IsNullOrEmpty(_config.AuthToken))
            _acct = await _account.MeAsync(_config.ServerUrl, _config.AuthToken!);
        if (_acct == null) { _config.AuthToken = null; _config.Save("app"); }
        ReflectAccount();
        StartHosting();
        if (_acct != null) await LoadComputersAsync();
    }

    private void ReflectAccount()
    {
        bool signedIn = _acct != null;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountBadge.Text = signedIn ? $"{_acct!.Email} · {_acct.OrgName}" : "";
        SignedInText.Text = signedIn ? $"Signed in as {_acct!.Email}\nOrganization: {_acct.OrgName}{(_acct.IsAdmin ? " (admin)" : "")}" : "";
        ListMsg.Text = signedIn ? "" : "Sign in to see your computers.";
        if (!signedIn) CompList.ItemsSource = null;
    }

    private async void SignInBtn_Click(object sender, RoutedEventArgs e)
    {
        AccountMsg.Text = "";
        var server = ServerBox.Text.Trim();
        _config.ServerUrl = server; _config.Save("app");
        try
        {
            _acct = await _account.LoginAsync(server, EmailBox.Text.Trim(), LoginPwBox.Password);
            _config.AuthToken = _acct.Token; _config.AccountEmail = _acct.Email; _config.OrgName = _acct.OrgName;
            _config.Save("app");
            LoginPwBox.Clear();
            ReflectAccount();
            StartHosting();                // re-register so this PC is claimed into the org
            await LoadComputersAsync();
        }
        catch (Exception ex) { AccountMsg.Text = ex.Message; }
    }

    private void SignOutBtn_Click(object sender, RoutedEventArgs e)
    {
        _acct = null;
        _config.AuthToken = null; _config.Save("app");
        ReflectAccount();
        StartHosting();                    // re-register anonymously
    }

    private void RegisterLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var u = new Uri(ServerBox.Text.Trim());
            var http = (u.Scheme == "wss" ? "https" : "http") + $"://{u.Host}:{u.Port}/";
            Process.Start(new ProcessStartInfo(http) { UseShellExecute = true });
        }
        catch (Exception ex) { AccountMsg.Text = "Couldn't open browser: " + ex.Message; }
    }

    private async Task LoadComputersAsync()
    {
        if (_acct == null) return;
        ListMsg.Text = "Loading…";
        try
        {
            var data = await _account.MyComputersAsync(_config.ServerUrl, _acct.Token);
            var gname = data.Groups.ToDictionary(g => g.Id, g => g.Name);
            var rows = data.Computers
                .Select(c => new CompRow(
                    c.RelayId,
                    string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name,
                    c.GroupId != null && gname.TryGetValue(c.GroupId, out var gn) ? gn : "Ungrouped",
                    c.Online,
                    c.Online ? (c.Busy ? "● In session" : "● Online") : "○ Offline"))
                .OrderByDescending(r => r.Online)
                .ThenBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CompRow.GroupName)));
            CompList.ItemsSource = view;
            ListMsg.Text = $"{rows.Count(r => r.Online)} online / {rows.Count} total";
        }
        catch (Exception ex) { ListMsg.Text = "Couldn't load: " + ex.Message; }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadComputersAsync();

    // ------------------------------- hosting (always on) -------------------------------

    private void HostSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        _config.ServerUrl = ServerBox.Text.Trim();
        _config.FixedPassword = HostPwBox.Text.Trim();
        _config.Save("app");
        StartHosting();
    }

    private void StartHosting()
    {
        var server = ServerBox.Text.Trim();
        var pw = HostPwBox.Text.Trim();
        var auth = _acct?.Token ?? "";
        if (pw.Length == 0) { HostStatus.Text = "Set a password to allow connections."; return; }
        if (server == _appliedServer && pw == _appliedPw && auth == _appliedAuth && _hostCts != null) return;

        _appliedServer = server; _appliedPw = pw; _appliedAuth = auth;
        _hostCts?.Cancel();
        _hostCts = new CancellationTokenSource();
        _ = HostLoopAsync(server, pw, _config.HostToken!, _acct?.Token, _hostCts.Token);
    }

    private async Task HostLoopAsync(string server, string pw, string token, string? authToken, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HostSession? session = null;
            var down = new TaskCompletionSource();
            try
            {
                session = new HostSession(server, pw, token: token, authToken: authToken, hostName: Environment.MachineName);
                _host = session;
                session.IdAssigned += id => Dispatcher.Invoke(() => IdBox.Text = FormatId(id));
                session.Status += s => Dispatcher.Invoke(() => HostStatus.Text = s);
                session.Status += s => { if (s.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase)) down.TrySetResult(); };
                await session.StartAsync();
                using (ct.Register(() => down.TrySetResult()))
                    await down.Task;
            }
            catch (Exception ex) { Dispatcher.Invoke(() => HostStatus.Text = "Connection error — retrying… " + ex.Message); }
            finally { try { session?.Dispose(); } catch { } if (_host == session) _host = null; }

            if (ct.IsCancellationRequested) break;
            Dispatcher.Invoke(() => IdBox.Text = "reconnecting…");
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    // ------------------------------- connecting out -------------------------------

    private void ManualConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = ConnIdBox.Text.Trim().Replace(" ", "");
        if (id.Length == 0) { ConnIdBox.Focus(); return; }
        OpenViewer(id, id, password: ConnPwBox.Text.Trim(), authToken: null);
    }

    private void CompList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CompList.SelectedItem is not CompRow row) return;
        if (!row.Online || row.RelayId == null) { ListMsg.Text = "That computer is offline."; return; }
        OpenViewer(row.RelayId, row.Display, password: null, authToken: _acct?.Token);
    }

    private void OpenViewer(string id, string display, string? password, string? authToken)
    {
        var w = new ViewerWindow(_config.ServerUrl, id, display, password: password, authToken: authToken) { Owner = this };
        w.Show();
    }

    // ------------------------------- updates -------------------------------

    private void StartUpdateChecks()
    {
        _ = CheckForUpdateAsync();
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_pendingUpdate != null) { _updateTimer?.Stop(); return; }
        var url = ServerBox.Text.Trim();
        if (url.Length == 0) return;
        var info = await _updater.CheckAsync(url);
        if (info == null) return;
        _pendingUpdate = info;
        _updateTimer?.Stop();
        UpdateBtn.Content = $"⬇ Update to v{info.Version}";
        UpdateBtn.ToolTip = string.IsNullOrWhiteSpace(info.Notes) ? null : info.Notes;
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } info) return;
        UpdateBtn.IsEnabled = false;
        try
        {
            HostStatus.Text = $"Downloading v{info.Version}…";
            var progress = new Progress<double>(p => Dispatcher.Invoke(() => HostStatus.Text = $"Downloading v{info.Version}… {(int)(p * 100)}%"));
            var stage = await _updater.DownloadAsync(ServerBox.Text.Trim(), info, progress);
            HostStatus.Text = "Installing — the app will restart…";
            _hostCts?.Cancel();
            if (_host != null) { try { await _host.StopAsync(); } catch { } }
            _updater.LaunchAndExit(stage);
            Application.Current.Shutdown();
        }
        catch (Exception ex) { HostStatus.Text = "Update failed: " + ex.Message; UpdateBtn.IsEnabled = true; }
    }

    // ------------------------------- misc -------------------------------

    private static string FormatId(string id)
        => id.Length == 9 ? $"{id[..3]} {id.Substring(3, 3)} {id[6..]}" : id;

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no l/o/0/1
        var chars = new char[6];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _hostCts?.Cancel();
        if (_host != null) { try { await _host.StopAsync(); } catch { } }
        base.OnClosed(e);
    }
}
