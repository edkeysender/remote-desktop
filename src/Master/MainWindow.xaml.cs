using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using RemoteDesktop.Client;   // HostSession (host engine)
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// Unified AllViewer desktop app. The hub UI is the approved AllViewer design (Appendix C)
/// rendered pixel-exact in a WebView2; C# bridges it to the account/host/connect logic.
/// Each remote session opens in a separate native <see cref="ViewerWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly AccountClient _account = new();
    private AccountSession? _acct;
    private MyComputers? _myComp;

    private HostSession? _host;
    private CancellationTokenSource? _hostCts;
    private string _appliedServer = "", _appliedPw = "", _appliedAuth = "";

    private readonly Updater _updater = new("app", elevate: false);
    private UpdateInfo? _pendingUpdate;
    private bool _updating;       // an update is downloading/applying (guard against re-entry)
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    // live state pushed to the web UI
    private string _id = "", _org = "", _status = "Starting…";
    private bool _online, _ready, _initDone;
    private string? _lastError;
    private string _brandName = "AllViewer", _brandAccent = "#5B5BF5";
    private string? _brandLogo;
    private string _groupId = "", _groupName = "";
    private readonly LanDiscovery _lan = new();
    private List<HostSession.FleetDevice> _relayFleet = new();   // org/group siblings from the relay

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("app");
        if (string.IsNullOrEmpty(_config.HostToken)) _config.HostToken = Guid.NewGuid().ToString("N");
        if (string.IsNullOrEmpty(_config.FixedPassword)) _config.FixedPassword = GeneratePassword();
        // Clear the update-attempt marker once we're actually running the target version (or newer).
        if (!string.IsNullOrEmpty(_config.UpdateTriedVersion) && Version.TryParse(_config.UpdateTriedVersion, out var tv)
            && Updater.Current >= new Version(tv.Major, tv.Minor, Math.Max(0, tv.Build)))
        { _config.UpdateTriedVersion = null; _config.UpdateTriedCount = 0; }
        _config.Save("app");
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try { await Web.EnsureCoreWebView2Async(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is required.\n\n" + ex.Message,
                "AllViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try { OnMessage(e.TryGetWebMessageAsString()); } catch { }
        };
        Web.CoreWebView2.NavigateToString(LoadHtml());

        _lan.Changed += () => Dispatcher.Invoke(PushState);   // refresh fleet as LAN peers come/go
        StartUpdateChecks();
        await RestoreSessionAsync();
        StartHosting();
        _initDone = true;
        ProcessPendingConnect();   // handle a allviewer:// deep link that launched us
    }

    private static string LoadHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("desktop.html", StringComparison.OrdinalIgnoreCase));
        if (name == null) return "<h1>UI resource missing</h1>";
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ------------------------------- bridge in -------------------------------

    private void OnMessage(string? json)
    {
        if (string.IsNullOrEmpty(json)) return;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var t = r.GetProperty("t").GetString();
        switch (t)
        {
            case "ready":
                _ready = true;
                PushState();
                break;
            case "connect":
                OpenViewer(r.GetProperty("id").GetString() ?? "", r.GetProperty("id").GetString() ?? "",
                    password: r.TryGetProperty("pw", out var pw) ? pw.GetString() : "", authToken: null);
                break;
            case "connectDevice":
            {
                var rid = r.GetProperty("relayId").GetString() ?? "";
                var nm = r.TryGetProperty("name", out var n) ? n.GetString() ?? rid : rid;
                OpenViewer(rid, nm, password: null, authToken: _acct?.Token);
                break;
            }
            case "login": _ = LoginAsync(r.GetProperty("email").GetString() ?? "", r.GetProperty("password").GetString() ?? ""); break;
            case "enroll": Enroll(r.GetProperty("token").GetString() ?? ""); break;
            case "regen": RegeneratePassword(); break;
            case "setting":
                if (r.GetProperty("key").GetString() == "consent")
                { _config.AskConsent = r.GetProperty("on").GetBoolean(); _config.Save("app"); }
                break;
            case "setpw": SetPassword(); break;
            case "setserver": SetServer(); break;
            case "update": _ = DoUpdateAsync(); break;
        }
    }

    // ------------------------------- bridge out -------------------------------

    private void PushState()
    {
        if (!_ready || Web?.CoreWebView2 == null) return;

        var groups = new Dictionary<string, List<object>>();
        var groupSeen = new HashSet<string>();   // relayIds already placed into a group (dedupe)

        // "Your fleet" = account computers (if signed in) + relay-pushed org/group siblings
        // (shown even when only enrolled — no user login needed), deduped by connect id / name.
        var rows = new List<(string Name, string? RelayId, bool Online, bool Busy)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRow(string name, string? relayId, bool online, bool busy)
        {
            var key = !string.IsNullOrEmpty(relayId) ? relayId! : "name:" + name;
            if (!seen.Add(key)) return;
            rows.Add((name, relayId, online, busy));
        }
        void AddToGroup(string groupName, string name, string? relayId, bool online)
        {
            if (!groups.TryGetValue(groupName, out var list)) { list = new(); groups[groupName] = list; }
            list.Add(new { name, relayId, online });
            if (!string.IsNullOrEmpty(relayId)) groupSeen.Add(relayId!);
        }
        if (_myComp != null)
        {
            var gname = _myComp.Groups.ToDictionary(g => g.Id, g => g.Name);
            foreach (var c in _myComp.Computers.OrderByDescending(c => c.Online).ThenBy(c => c.Name))
            {
                AddRow(c.Name, c.RelayId, c.Online, c.Busy);
                AddToGroup(c.GroupId != null && gname.TryGetValue(c.GroupId, out var gn) ? gn : "Ungrouped",
                           c.Name, c.RelayId, c.Online);
            }
        }
        // Enrolled-only devices learn their group's siblings from the relay — surface those in
        // the address book too, under this device's group.
        var myGroup = string.IsNullOrEmpty(_groupName) ? "Ungrouped" : _groupName;
        foreach (var d in _relayFleet)
        {
            AddRow(d.Name, d.RelayId, d.Online, false);
            if (string.IsNullOrEmpty(d.RelayId) || !groupSeen.Contains(d.RelayId!))
                AddToGroup(myGroup, d.Name, d.RelayId, d.Online);
        }

        object[] fleet = rows.OrderByDescending(c => c.Online).ThenBy(c => c.Name)
            .Select(c => (object)new { name = c.Name, relayId = c.RelayId, online = c.Online, busy = c.Busy }).ToArray();

        var state = new
        {
            t = "state",
            id = _id,
            password = _config.FixedPassword,
            hostName = Environment.MachineName,
            os = RuntimeInformation.OSDescription,
            org = string.IsNullOrEmpty(_org) ? null : _org,
            signedIn = _acct != null,
            email = _acct?.Email,
            status = _status,
            online = _online,
            version = Updater.Current.ToString(),
            updateAvailable = _pendingUpdate?.Version.ToString(),
            consent = _config.AskConsent,
            brand = new { name = _brandName, accent = _brandAccent, logo = _brandLogo },
            computers = fleet,
            localDevices = _lan.GetDevices()
                .Where(d => (string.IsNullOrEmpty(_org) || d.Org == _org) && (string.IsNullOrEmpty(_groupId) || d.GroupId == _groupId))
                .Select(d => (object)new { name = d.Name, relayId = d.Id, online = true, group = d.GroupName, lan = true })
                .ToArray(),
            groups,
            recent = BuildRecent(),
            alerts = Array.Empty<object>(),
            error = _lastError,
        };
        _lastError = null;
        try { Web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(state)); } catch { }
    }

    // ------------------------------- account -------------------------------

    private async Task RestoreSessionAsync()
    {
        if (!string.IsNullOrEmpty(_config.AuthToken))
            _acct = await _account.MeAsync(_config.ServerUrl, _config.AuthToken!);
        if (_acct == null) { _config.AuthToken = null; _config.Save("app"); }
        else { _org = _acct.OrgName; await LoadComputersAsync(); }
        PushState();
    }

    private async Task LoginAsync(string email, string password)
    {
        try
        {
            _acct = await _account.LoginAsync(_config.ServerUrl, email, password);
            _config.AuthToken = _acct.Token; _config.AccountEmail = _acct.Email; _config.OrgName = _acct.OrgName;
            _config.Save("app");
            _org = _acct.OrgName;
            StartHosting();               // re-register so this PC is claimed into the org
            await LoadComputersAsync();
        }
        catch (Exception ex) { _lastError = ex.Message; }
        PushState();
    }

    private void Enroll(string token)
    {
        _config.EnrollToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _config.Save("app");
        StartHosting();
        PushState();
    }

    private async Task LoadComputersAsync()
    {
        if (_acct == null) { _myComp = null; return; }
        try { _myComp = await _account.MyComputersAsync(_config.ServerUrl, _acct.Token); }
        catch { _myComp = null; }
    }

    // ------------------------------- hosting (always on) -------------------------------

    private void StartHosting()
    {
        var server = _config.ServerUrl;
        var pw = (_config.FixedPassword ?? "").Trim();
        var auth = _acct?.Token ?? "";
        var enroll = string.IsNullOrEmpty(auth) ? (_config.EnrollToken ?? "") : "";
        var key = server + "|" + pw + "|" + auth + "|" + enroll;
        if (pw.Length == 0) { _status = "Set a password to allow connections."; PushState(); return; }
        if (key == _appliedServer + "|" + _appliedPw + "|" + _appliedAuth && _hostCts != null) return;
        _appliedServer = server; _appliedPw = pw; _appliedAuth = auth + "|" + enroll;

        _hostCts?.Cancel();
        _hostCts = new CancellationTokenSource();
        _ = HostLoopAsync(server, pw, _config.HostToken!, _acct?.Token,
                          string.IsNullOrEmpty(enroll) ? null : enroll, _hostCts.Token);
    }

    private async Task HostLoopAsync(string server, string pw, string token, string? authToken, string? enrollToken, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HostSession? session = null;
            var down = new TaskCompletionSource();
            try
            {
                session = new HostSession(server, pw, token: token, authToken: authToken, hostName: Environment.MachineName, enrollToken: enrollToken);
                _host = session;
                session.IdAssigned += id => Dispatcher.Invoke(() => { _id = id; RefreshDiscovery(); PushState(); });
                session.OrgAssigned += org => Dispatcher.Invoke(() => { _org = org ?? ""; RefreshDiscovery(); PushState(); });
                session.GroupAssigned += (gid, gname) => Dispatcher.Invoke(() => { _groupId = gid ?? ""; _groupName = gname ?? ""; RefreshDiscovery(); PushState(); });
                session.FleetUpdated += list => Dispatcher.Invoke(() => { _relayFleet = list; PushState(); });
                session.Branding += (name, accent, logo) => Dispatcher.Invoke(() =>
                {
                    _brandName = string.IsNullOrWhiteSpace(name) ? "AllViewer" : name!;
                    _brandAccent = string.IsNullOrWhiteSpace(accent) ? "#5B5BF5" : accent!;
                    _brandLogo = logo;
                    Title = _brandName;
                    PushState();
                });
                session.Status += s => Dispatcher.Invoke(() => { _status = s; _online = s.Contains("Ready") || s.Contains("active") || s.Contains("streaming"); PushState(); });
                session.Status += s => { if (s.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase)) down.TrySetResult(); };
                await session.StartAsync();
                using (ct.Register(() => down.TrySetResult()))
                    await down.Task;
            }
            catch (Exception ex) { Dispatcher.Invoke(() => { _status = "Reconnecting… " + ex.Message; _online = false; PushState(); }); }
            finally { try { session?.Dispose(); } catch { } if (_host == session) _host = null; }

            if (ct.IsCancellationRequested) break;
            Dispatcher.Invoke(() => { _online = false; _status = "Reconnecting…"; PushState(); });
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    // Announce ourselves on the LAN and collect same-network peers (needs our id).
    private void RefreshDiscovery()
    {
        if (!string.IsNullOrEmpty(_id)) _lan.Start(_id, Environment.MachineName, _org, _groupId, _groupName);
    }

    private void RegeneratePassword()
    {
        _config.FixedPassword = GeneratePassword();
        _config.Save("app");
        StartHosting();
        PushState();
    }

    // ------------------------------- connecting out -------------------------------

    private void OpenViewer(string id, string display, string? password, string? authToken, string? serverOverride = null)
    {
        id = id.Replace(" ", "");
        if (id.Length == 0) return;
        RecordRecent(string.IsNullOrWhiteSpace(display) ? id : display, id);
        var server = string.IsNullOrWhiteSpace(serverOverride) ? _config.ServerUrl : serverOverride!;
        var w = new ViewerWindow(server, id, display, password: password, authToken: authToken,
                                 peerToken: _config.HostToken, fleetProvider: CurrentFleetSnapshot) { Owner = this };
        w.Show();
    }

    // Recent connections for the app's Recent list; mark each online if it's reachable now.
    private object[] BuildRecent()
    {
        var online = new HashSet<string>(CurrentFleetSnapshot().Where(f => f.Online && !string.IsNullOrEmpty(f.RelayId)).Select(f => f.RelayId!));
        return _config.Recent.Select(r => (object)new
        {
            name = r.Name, relayId = r.RelayId, online = online.Contains(r.RelayId), when = RelWhen(r.WhenUnixMs)
        }).ToArray();
    }

    private static string RelWhen(long ms)
    {
        if (ms <= 0) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var s = (DateTimeOffset.UtcNow - dt).TotalSeconds;
        if (s < 60) return "just now";
        if (s < 3600) return $"{(int)(s / 60)}m ago";
        if (s < 86400) return $"{(int)(s / 3600)}h ago";
        return dt.LocalDateTime.ToString("MMM d");
    }

    private void RecordRecent(string name, string relayId)
    {
        if (string.IsNullOrWhiteSpace(relayId)) return;
        _config.Recent.RemoveAll(r => r.RelayId == relayId);
        _config.Recent.Insert(0, new RecentConnection { Name = name, RelayId = relayId, WhenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        if (_config.Recent.Count > 12) _config.Recent.RemoveRange(12, _config.Recent.Count - 12);
        _config.Save("app");
        PushState();
    }

    // Snapshot of devices this app can reach right now (account computers + relay siblings +
    // LAN peers), deduped — lets the session window's device switcher work without a sign-in.
    private IReadOnlyList<(string Name, string? RelayId, bool Online)> CurrentFleetSnapshot()
    {
        var list = new List<(string, string?, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? rid, bool online)
        {
            var key = !string.IsNullOrEmpty(rid) ? rid! : "n:" + name;
            if (seen.Add(key)) list.Add((name, rid, online));
        }
        if (_myComp != null) foreach (var c in _myComp.Computers) Add(c.Name, c.RelayId, c.Online);
        foreach (var d in _relayFleet) Add(d.Name, d.RelayId, d.Online);
        foreach (var d in _lan.GetDevices()
                     .Where(d => (string.IsNullOrEmpty(_org) || d.Org == _org) && (string.IsNullOrEmpty(_groupId) || d.GroupId == _groupId)))
            Add(d.Name, d.Id, true);
        return list;
    }

    // ---- deep link: allviewer://connect?id=<relayId>&server=<ws>&name=<name> ----
    private string? _pendingUri;

    public void HandleConnectUri(string uri)
    {
        _pendingUri = uri;
        if (_initDone) ProcessPendingConnect();   // else it runs when startup finishes
    }

    private void ProcessPendingConnect()
    {
        if (_pendingUri is not { } uri) return;
        _pendingUri = null;
        try
        {
            var u = new Uri(uri);
            var q = ParseQuery(u.Query);
            if (!q.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id)) return;
            var name = q.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : id;
            var server = q.TryGetValue("server", out var s) && !string.IsNullOrWhiteSpace(s) ? s : _config.ServerUrl;
            // Account token lets it connect password-less to a fleet device.
            OpenViewer(id, name, password: null, authToken: _acct?.Token, serverOverride: server);
        }
        catch { /* ignore malformed deep link */ }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }

    // ------------------------------- small prompts -------------------------------

    private void SetPassword()
    {
        var v = Prompt("Change connection password", "New password:", _config.FixedPassword ?? "");
        if (v == null) return;
        _config.FixedPassword = v.Trim().Length == 0 ? GeneratePassword() : v.Trim();
        _config.Save("app");
        StartHosting();
        PushState();
    }

    private void SetServer()
    {
        var v = Prompt("Signaling server", "Server URL (ws://host:port):", _config.ServerUrl);
        if (v == null || v.Trim().Length == 0) return;
        _config.ServerUrl = v.Trim();
        _config.Save("app");
        _appliedServer = "";   // force host restart
        StartHosting();
        PushState();
    }

    // Minimal modal text prompt (WPF).
    private string? Prompt(string title, string label, string initial)
    {
        var win = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var box = new System.Windows.Controls.TextBox { Text = initial, Padding = new Thickness(6) };
        panel.Children.Add(box);
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(row);
        win.Content = panel;
        box.Focus(); box.SelectAll();
        return win.ShowDialog() == true ? result : null;
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
        var info = await _updater.CheckAsync(_config.ServerUrl);
        if (info == null) return;
        // Already tried this exact version and we're still not on it (the served build/manifest
        // are out of sync, or the swap didn't take) — stop nagging for it. A genuinely newer
        // version still shows. The marker clears on startup once we actually reach that version.
        if (info.Version.ToString() == _config.UpdateTriedVersion) { _updateTimer?.Stop(); return; }
        _pendingUpdate = info;
        _updateTimer?.Stop();
        PushState();        // surfaces the "update available" toast; the user clicks to apply
    }

    private async Task DoUpdateAsync()
    {
        if (_pendingUpdate is not { } info || _updating) return;
        _updating = true;
        // Remember what we're applying so a version that can't actually be reached stops nagging.
        _config.UpdateTriedVersion = info.Version.ToString();
        _config.Save("app");
        try
        {
            _status = $"Downloading v{info.Version}…"; PushState();
            var stage = await _updater.DownloadAsync(_config.ServerUrl, info);
            _status = "Installing — the app will restart…"; PushState();
            _hostCts?.Cancel();
            if (_host != null) { try { await _host.StopAsync(); } catch { } }
            _updater.LaunchAndExit(stage);
            Application.Current.Shutdown();
        }
        catch (Exception ex) { _updating = false; _lastError = "Update failed: " + ex.Message; PushState(); }
    }

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
        _lan.Stop();
        if (_host != null) { try { await _host.StopAsync(); } catch { } }
        base.OnClosed(e);
    }
}
