using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// A single remote-control session in its own window. Opened by the hub with a target
/// (server + id) and one auth mode: an account token (password-less), an admin/directory
/// password, or a per-host password. Closing the window ends the session.
/// The chrome follows the AllViewer design (Appendix D) — dark by default, light on toggle.
/// </summary>
public partial class ViewerWindow : Window
{
    private readonly string _serverUrl, _password;
    private string _id, _display;                 // mutable: the device switcher reconnects in place
    private readonly string? _adminPw, _authToken, _peerToken;
    private readonly Func<IReadOnlyList<(string Name, string? RelayId, bool Online)>>? _fleetProvider;
    private AccountClient? _account;

    private ViewerSession? _session;
    private bool _connected;
    private int _srcW = 1, _srcH = 1;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private long _lastMoveMs;

    private List<RemoteMonitor> _monitors = new();
    private int _currentMon;
    private double _zoom;   // 0 = Fit, else scale factor
    private int _lastFilePct = -1;

    private double _panVx, _panVy;
    private System.Windows.Threading.DispatcherTimer? _panTimer;
    private readonly Stopwatch _panClock = new();

    private bool _dark = true;
    private readonly Stopwatch _sessionClock = new();
    private System.Windows.Threading.DispatcherTimer? _timerTick;
    private System.Windows.Threading.DispatcherTimer? _pingTimer;

    // monitor rail + minimap + overview
    private readonly Dictionary<int, BitmapSource> _thumbImg = new();
    private readonly Dictionary<int, Image> _railImg = new();
    private readonly Dictionary<int, Image> _ovImg = new();
    private bool _railExpanded;
    private Canvas? _miniCanvas;
    private System.Windows.Shapes.Rectangle? _miniRect;
    private int _miniIndex = -2;
    private bool _miniDragging;
    private bool _switching;                          // a device handoff is in progress
    private readonly HashSet<int> _newWin = new();   // monitors with an unseen new window
    private IntPtr _hwnd;
    private bool _clipHooked;

    public ViewerWindow(string serverUrl, string id, string display,
                        string? password = null, string? adminPw = null, string? authToken = null, string? peerToken = null,
                        Func<IReadOnlyList<(string Name, string? RelayId, bool Online)>>? fleetProvider = null)
    {
        InitializeComponent();
        _serverUrl = serverUrl; _id = id; _display = display;
        _password = password ?? ""; _adminPw = adminPw; _authToken = authToken; _peerToken = peerToken;
        _fleetProvider = fleetProvider;
        Title = $"AllViewer — {display}";
        TitleText.Text = display;
        Loaded += async (_, _) => await StartSessionAsync();
    }

    // ---- clipboard sync (local → remote) ----
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        try { _clipHooked = AddClipboardFormatListener(_hwnd); } catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // Any local clipboard change (Ctrl+C in any app) pushes text to the remote so a
        // remote Ctrl+V pastes it. Files are handled at paste time (see ForwardKey).
        if (msg == WM_CLIPBOARDUPDATE) PushLocalClipboardText();
        return IntPtr.Zero;
    }

    private void PushLocalClipboardText()
    {
        if (!_connected || _session == null) return;
        try { if (Clipboard.ContainsText()) { var t = Clipboard.GetText(); if (!string.IsNullOrEmpty(t)) _session.SendClipboard(t); } }
        catch { /* clipboard briefly locked by another app */ }
    }

    private async Task StartSessionAsync()
    {
        _session = new ViewerSession();
        _session.Connected += () => Dispatcher.Invoke(OnConnected);
        _session.Rejected  += r  => Dispatcher.Invoke(() => { SetStatus("Rejected: " + r, "#F5484D"); CloseSoon(); });
        _session.Frame     += DrawFrame;
        _session.Monitors  += (mons, cur) => Dispatcher.Invoke(() => PopulateMonitors(mons, cur));
        _session.Thumbnail += (i, _, _, bytes) => Dispatcher.Invoke(() => OnThumb(i, bytes));
        _session.NewWindow += i => Dispatcher.Invoke(() => OnNewWindow(i));
        _session.PongReceived += ts => Dispatcher.Invoke(() => OnPong(ts));
        _session.Closed    += r  => Dispatcher.Invoke(() => { if (_connected) SetStatus("Closed: " + (r ?? ""), "#8A8DA3"); CloseSoon(); });

        _session.Files.SendProgress     += (n, done, total) => Dispatcher.Invoke(() => TxProgress(n, true, done, total));
        _session.Files.ReceiveStarted   += (n, total)       => Dispatcher.Invoke(() => TxProgress(n, false, 0, total));
        _session.Files.ReceiveProgress  += (n, done, total) => Dispatcher.Invoke(() => TxProgress(n, false, done, total));
        _session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() =>
            { TxComplete(n, false); SetRowLocalPath(n, false, path); SetStatus($"Received {n} → {Path.GetDirectoryName(path)}"); });
        _session.Files.Error            += msg => Dispatcher.Invoke(() =>
            { TxError(msg); SetStatus("File transfer failed: " + msg, "#F5484D"); });

        try
        {
            SetStatus("Connecting…");
            await _session.ConnectAsync(_serverUrl, _id, _password, _adminPw, _authToken, _peerToken);
        }
        catch (Exception ex)
        {
            SetStatus("Connect failed: " + ex.Message, "#F5484D");
            CloseSoon();
        }
    }

    private bool _closing;
    private void CloseSoon()
    {
        // During a device switch the OLD session's teardown fires Closed → don't let that
        // close the whole window (that was the switch crash). OnConnected clears _switching.
        if (_switching || _closing) return;
        _closing = true;
        // Give the user a moment to read a rejection/close reason, then close the window.
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }

    private void OnConnected()
    {
        _connected = true;
        _switching = false;                // handoff finished; normal close behavior resumes
        SetStatus("Connected", "#1FC98B");
        Hint.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        foreach (var b in new[] { ZoomFit, Zoom100, Zoom200, TransferBtn, WinKeyBtn, ClipboardBtn }) b.IsEnabled = true;
        ClipboardBtn.ToolTip = "Send your clipboard text to the remote PC";
        SetZoom(0);   // Fit

        P2pText.Text = "Live";
        _pingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pingTimer.Tick += (_, _) => _session?.Ping(_sessionClock.ElapsedMilliseconds);
        _pingTimer.Start();

        _sessionClock.Restart();
        _timerTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timerTick.Tick += (_, _) =>
        {
            var e = _sessionClock.Elapsed;
            TimerText.Text = e.TotalHours >= 1 ? $"{(int)e.TotalHours}:{e.Minutes:00}:{e.Seconds:00}" : $"{e.Minutes:00}:{e.Seconds:00}";
        };
        _timerTick.Start();
        PushLocalClipboardText();   // make anything already copied available to paste remotely
        RemoteImage.Focus();
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string s, string? dotHex = null)
    {
        StatusText.Text = s;
        if (dotHex != null) StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dotHex));
    }

    // ---- device switcher (Phase E) ----
    private async void DeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        DevList.Children.Clear();
        DevScope.Text = "Loading devices…";
        DevPopup.IsOpen = true;

        // Signed-in account → the full directory (all groups). Otherwise fall back to the
        // local fleet the app already knows (relay siblings + LAN), so an enrolled-only
        // device can still switch between its group's computers.
        if (!string.IsNullOrEmpty(_authToken))
        {
            _account ??= new AccountClient();
            try { BuildDeviceMenu(await _account.MyComputersAsync(_serverUrl, _authToken!)); }
            catch (Exception ex) { DevScope.Text = "Could not load devices: " + ex.Message; }
        }
        else if (_fleetProvider != null)
        {
            var comps = _fleetProvider()
                .Select(f => new AccountComputer("", f.Name, null, f.RelayId, f.Online, false)).ToList();
            BuildDeviceMenu(new MyComputers(Array.Empty<DirectoryGroup>(), comps, false));
        }
        else DevScope.Text = "Sign in to switch between devices.";
    }

    private void BuildDeviceMenu(MyComputers my)
    {
        DevList.Children.Clear();
        var gname = new Dictionary<string, string>();
        foreach (var g in my.Groups) gname[g.Id] = g.Name;
        var ordered = my.Computers.OrderByDescending(c => c.Online).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        DevScope.Text = ordered.Count == 0 ? "No devices you can reach."
                      : my.Admin ? "All devices in your organization" : "Devices in your groups";
        foreach (var c in ordered) DevList.Children.Add(MakeDeviceRow(c, gname));
    }

    private Border MakeDeviceRow(AccountComputer c, Dictionary<string, string> gname)
    {
        bool current = c.Online && c.RelayId == _id;
        bool connectable = c.Online && !c.Busy && !string.IsNullOrEmpty(c.RelayId) && !current;

        var led = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            Fill = new SolidColorBrush(current ? Color.FromRgb(0x3E, 0xC8, 0xFF)
                                     : c.Online ? Color.FromRgb(0x1F, 0xC9, 0x8B) : Color.FromRgb(0xF5, 0x48, 0x4D))
        };

        var name = new TextBlock { Text = c.Name, Foreground = new SolidColorBrush(Res("TextBrush")), FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        string grp = c.GroupId != null && gname.TryGetValue(c.GroupId, out var gn) ? "  ·  " + gn : "";
        var meta = new TextBlock
        {
            Text = (c.Online && !string.IsNullOrEmpty(c.RelayId) ? FormatId(c.RelayId!) : "offline") + grp,
            Foreground = new SolidColorBrush(Res("MutedBrush")), FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0)
        };
        var left = new StackPanel();
        left.Children.Add(name); left.Children.Add(meta);

        (string txt, Color fg) = current ? ("Viewing", Color.FromRgb(0x3E, 0xC8, 0xFF))
            : !c.Online ? ("Offline", Color.FromRgb(0x8A, 0x8D, 0xA3))
            : c.Busy ? ("In use", Color.FromRgb(0xFF, 0xAA, 0x1D))
            : ("Unattended", Color.FromRgb(0x1F, 0xC9, 0x8B));
        var badge = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x22, fg.R, fg.G, fg.B)),
            Child = new TextBlock { Text = txt, Foreground = new SolidColorBrush(fg), FontSize = 10.5, FontWeight = FontWeights.SemiBold }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(led, 0); Grid.SetColumn(left, 1); Grid.SetColumn(badge, 2);
        grid.Children.Add(led); grid.Children.Add(left); grid.Children.Add(badge);

        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent, Child = grid, Opacity = connectable || current ? 1.0 : 0.55,
            Cursor = connectable ? Cursors.Hand : Cursors.Arrow
        };
        if (connectable)
        {
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Res("CardBrush"));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => SwitchDevice(c.RelayId!, c.Name);
        }
        return row;
    }

    private static string FormatId(string id)
    {
        id = id.Replace(" ", "");
        return id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
    }

    private async void SwitchDevice(string id, string name)
    {
        DevPopup.IsOpen = false;
        if (id == _id) return;
        if (AnyActive()) { SetStatus("Finish the file transfer before switching devices.", "#FFAA1D"); return; }

        _switching = true;                 // suppress the old session's Closed → CloseSoon
        var old = _session; _session = null;
        _connected = false;
        _pingTimer?.Stop(); _timerTick?.Stop();
        if (old != null) _ = Task.Run(async () => { try { await old.CloseAsync(); } catch { } old.Dispose(); });

        // reset all per-session UI/state, then reconnect (server logs session.end for A, session.start for B)
        _monitors = new(); _currentMon = 0; _thumbImg.Clear(); _newWin.Clear();
        RailList.Children.Clear(); _railImg.Clear(); MonitorPanel.Children.Clear();
        _miniCanvas = null; _miniRect = null; _miniIndex = -2;
        TrayList.Children.Clear(); _tx.Clear(); Tray.Visibility = Visibility.Collapsed; TrayChip.Visibility = Visibility.Collapsed; _trayClosed = false;
        CloseOverview();
        Scroller.Visibility = Visibility.Collapsed; Hint.Visibility = Visibility.Visible; Hint.Text = $"Connecting to {name}…";
        RemoteImage.Source = null; _wb = null;
        foreach (var b in new[] { ZoomFit, Zoom100, Zoom200, TransferBtn, WinKeyBtn, ClipboardBtn, OverviewBtn }) b.IsEnabled = false;

        _id = id; _display = name;
        TitleText.Text = name; Title = $"AllViewer — {name}";
        P2pText.Text = "Connecting"; P2pDot.Fill = new SolidColorBrush(Color.FromRgb(0x3E, 0xC8, 0xFF));
        TimerText.Text = "00:00";
        SetStatus("Connecting…", "#8A8DA3");

        await StartSessionAsync();
    }

    // ---- monitor picker (moves into the left rail in Phase B) ----
    private void PopulateMonitors(List<RemoteMonitor> mons, int current)
    {
        _monitors = mons; _currentMon = current;
        OverviewBtn.IsEnabled = mons.Count > 0;
        RebuildMonitorButtons(); RebuildRail(); UpdateReadout();
    }

    private void RebuildMonitorButtons()
    {
        MonitorPanel.Children.Clear();
        if (_monitors.Count > 1)
            MonitorPanel.Children.Add(MakeMonButton(-1, "All", "Show all monitors side by side"));
        foreach (var m in _monitors)
            MonitorPanel.Children.Add(MakeMonButton(m.Index, (m.Index + 1).ToString(),
                $"Display {m.Index + 1} — {m.Width}×{m.Height}{(m.Primary ? " (primary)" : "")}"));
    }

    private Color Res(string key) => ((SolidColorBrush)Resources[key]).Color;

    private Button MakeMonButton(int index, string label, string tooltip)
    {
        var btn = new Button { Content = label, Tag = index, ToolTip = tooltip, Style = (Style)FindResource("MonBtn") };
        bool selected = index == _currentMon;
        btn.Background = selected ? new SolidColorBrush(Color.FromRgb(0x5B, 0x5B, 0xF5)) : new SolidColorBrush(Res("CardBrush"));
        btn.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0xFF)) : new SolidColorBrush(Res("LineBrush"));
        btn.Foreground = selected ? Brushes.White : new SolidColorBrush(Res("MutedBrush"));
        btn.Click += (_, _) =>
        {
            if (!_connected || index == _currentMon) return;
            _currentMon = index;
            _session?.SelectMonitor(index);
            RebuildMonitorButtons();
            SetZoom(0);   // reset view on monitor switch
        };
        return btn;
    }

    // ---- zoom + pan ----
    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
            SetZoom(tag switch { "1" => 1, "2" => 2, _ => 0 });
    }

    private void SetZoom(double z)
    {
        _zoom = z;
        // highlight the active segment
        foreach (var (btn, val) in new[] { (ZoomFit, 0.0), (Zoom100, 1.0), (Zoom200, 2.0) })
        {
            bool on = z == val;
            btn.Background = on ? new SolidColorBrush(Res("AccentBrush")) : Brushes.Transparent;
            btn.Foreground = on ? Brushes.White : new SolidColorBrush(Res("MutedBrush"));
        }
        ApplyZoom();
        UpdateReadout();
    }

    private void ApplyZoom()
    {
        if (Scroller == null) return;
        if (_zoom <= 0)
        {
            StopPan();
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RemoteImage.Stretch = Stretch.Uniform;
            RemoteImage.LayoutTransform = Transform.Identity;
        }
        else
        {
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RemoteImage.Stretch = Stretch.None;
            RemoteImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        }
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_connected) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // cycle Fit -> 100% -> 200%
            double next = _zoom <= 0 ? (e.Delta > 0 ? 1 : 0) : _zoom >= 2 ? (e.Delta > 0 ? 2 : 1) : (e.Delta > 0 ? 2 : 0);
            SetZoom(next);
            return;
        }
        _session!.MouseWheel(e.Delta > 0 ? 120 : -120);
    }

    private const double PanMaxSpeed = 650;
    private const double PanBand = 14;   // narrow band so view-pan doesn't fight the forwarded remote cursor

    private void UpdateEdgePan(MouseEventArgs e)
    {
        var p = e.GetPosition(Scroller);
        double vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0) { StopPan(); return; }
        double fx = 0, fy = 0;
        if (p.X < PanBand) fx = -(PanBand - p.X) / PanBand;
        else if (p.X > vw - PanBand) fx = (p.X - (vw - PanBand)) / PanBand;
        if (p.Y < PanBand) fy = -(PanBand - p.Y) / PanBand;
        else if (p.Y > vh - PanBand) fy = (p.Y - (vh - PanBand)) / PanBand;
        _panVx = Math.Clamp(fx, -1, 1) * PanMaxSpeed;
        _panVy = Math.Clamp(fy, -1, 1) * PanMaxSpeed;
        ShowEdges();
        if (_panVx == 0 && _panVy == 0) return;
        if (_panTimer == null)
        {
            _panClock.Restart();
            _panTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _panTimer.Tick += PanTick;
            _panTimer.Start();
        }
    }

    private void ShowEdges()
    {
        // dim an edge once panning can go no further in that direction
        bool canL = Scroller.HorizontalOffset > 0.5, canR = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 0.5;
        bool canU = Scroller.VerticalOffset > 0.5,   canD = Scroller.VerticalOffset < Scroller.ScrollableHeight - 0.5;
        Edge(EdgeLeft,   _panVx < 0 && canL);
        Edge(EdgeRight,  _panVx > 0 && canR);
        Edge(EdgeTop,    _panVy < 0 && canU);
        Edge(EdgeBottom, _panVy > 0 && canD);
    }

    private static void Edge(UIElement el, bool on) => el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void PanTick(object? sender, EventArgs e)
    {
        double dt = _panClock.Elapsed.TotalSeconds; _panClock.Restart();
        if (_panVx == 0 && _panVy == 0) { StopPan(); return; }
        if (_panVx != 0) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + _panVx * dt);
        if (_panVy != 0) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + _panVy * dt);
        ShowEdges();
    }

    private void StopPan()
    {
        _panVx = _panVy = 0; _panTimer?.Stop(); _panTimer = null;
        Edge(EdgeLeft, false); Edge(EdgeRight, false); Edge(EdgeTop, false); Edge(EdgeBottom, false);
    }

    private void Scroller_MouseLeave(object sender, MouseEventArgs e) => StopPan();

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateReadout();

    private void UpdateReadout()
    {
        if (MonReadout == null) return;
        if (!_connected) { MonReadout.Text = "—"; return; }
        string mon = _currentMon == -1 ? "All displays" : $"Display {_currentMon + 1}";
        string view;
        if (_zoom <= 0) view = "fit";
        else
        {
            int vw = (int)Math.Round(Math.Min(_srcW, Scroller.ViewportWidth / _zoom));
            int vh = (int)Math.Round(Math.Min(_srcH, Scroller.ViewportHeight / _zoom));
            view = $"viewing {vw}×{vh}";
        }
        MonReadout.Text = $"{mon} · {_srcW}×{_srcH} · {view}";
        UpdateMinimap();
    }

    // ---- theme ----
    private void ThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_dark);

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        // Resource brushes declared in XAML are frozen, so mutating .Color throws — replace
        // the whole entry instead; DynamicResource references re-resolve to the new brush.
        void Set(string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Resources[key] = b;
        }
        if (dark)
        {
            Set("StageBrush", "#05060B"); Set("ChromeBrush", "#0B0C15"); Set("CardBrush", "#1A1C2B");
            Set("LineBrush", "#232538");  Set("TextBrush", "#F3F4F9");   Set("MutedBrush", "#8A8DA3");
            ThemeBtn.Content = "☾";
        }
        else
        {
            Set("StageBrush", "#E9EAF0"); Set("ChromeBrush", "#FFFFFF"); Set("CardBrush", "#F5F6F9");
            Set("LineBrush", "#E8E9F0");  Set("TextBrush", "#0B0C15");   Set("MutedBrush", "#6E7185");
            ThemeBtn.Content = "☀";
        }
        RebuildMonitorButtons();
        RebuildRail();
        SetZoom(_zoom);   // refresh segment colours for the new theme
    }

    // ---- monitor rail + minimap + all-monitors overview (Phase B) ----
    private void OnThumb(int index, byte[] jpeg)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(jpeg); bi.EndInit(); bi.Freeze();
            _thumbImg[index] = bi;
            if (_railImg.TryGetValue(index, out var im)) im.Source = bi;
            if (_ovImg.TryGetValue(index, out var im2)) im2.Source = bi;
        }
        catch { /* ignore a bad thumbnail */ }
    }

    private void OnNewWindow(int index)
    {
        if (index == _currentMon || !_monitors.Any(m => m.Index == index)) return;
        if (!_newWin.Add(index)) return;
        RebuildRail();
        if (Overview.Visibility == Visibility.Visible) BuildOverview();
    }

    private void UpdateRailBadge()
    {
        bool alert = _newWin.Count > 0;
        RailBadge.Background = new SolidColorBrush(alert ? Color.FromRgb(0xFF, 0xAA, 0x1D) : Res("AccentBrush"));
        RailBadgeText.Text = alert ? "!" : _monitors.Count.ToString();
    }

    private void OnPong(long ts)
    {
        long rtt = _sessionClock.ElapsedMilliseconds - ts;
        if (rtt < 0 || rtt > 10000) return;
        P2pText.Text = $"Live · {rtt} ms";
        var c = rtt < 80 ? Color.FromRgb(0x1F, 0xC9, 0x8B) : rtt < 200 ? Color.FromRgb(0x3E, 0xC8, 0xFF) : Color.FromRgb(0xFF, 0xAA, 0x1D);
        P2pDot.Fill = new SolidColorBrush(c);
    }

    private void RailToggle_Click(object sender, RoutedEventArgs e)
    {
        _railExpanded = !_railExpanded;
        Rail.Width = _railExpanded ? 196 : 56;
        RebuildRail();
    }

    private void RebuildRail()
    {
        if (RailList == null) return;
        RailList.Children.Clear(); _railImg.Clear();
        _miniCanvas = null; _miniRect = null; _miniIndex = -2;
        UpdateRailBadge();
        double dispW = _railExpanded ? 172 : 40;
        foreach (var m in _monitors)
        {
            double dispH = Math.Max(24, dispW * m.Height / Math.Max(1, m.Width));
            bool active = m.Index == _currentMon;
            bool flagged = _newWin.Contains(m.Index);
            int idx = m.Index;

            var img = new Image { Stretch = Stretch.Fill };
            if (_thumbImg.TryGetValue(idx, out var src)) img.Source = src;
            _railImg[idx] = img;

            var overlay = new Grid();
            overlay.Children.Add(img);
            if (flagged)
                overlay.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 10, Height = 10, Margin = new Thickness(0, 4, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x1D))
                });
            if (active && _railExpanded)
            {
                var cv = new Canvas { Width = dispW, Height = dispH, Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.SizeAll };
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x3E, 0xC8, 0xFF)), StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3E, 0xC8, 0xFF)), Visibility = Visibility.Collapsed
                };
                cv.Children.Add(rect);
                cv.MouseLeftButtonDown += MiniDown; cv.MouseMove += MiniMove; cv.MouseLeftButtonUp += MiniUp;
                overlay.Children.Add(cv);
                _miniCanvas = cv; _miniRect = rect; _miniIndex = idx;
            }

            var frame = new Border
            {
                Width = dispW, Height = dispH, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                BorderThickness = new Thickness(active || flagged ? 2 : 1),
                BorderBrush = new SolidColorBrush(active ? Color.FromRgb(0x5B, 0x5B, 0xF5)
                                                : flagged ? Color.FromRgb(0xFF, 0xAA, 0x1D) : Res("LineBrush")),
                Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x11, 0x17)),
                Child = overlay, Cursor = Cursors.Hand
            };
            frame.MouseLeftButtonUp += (_, _) => SwitchTo(idx);

            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            card.Children.Add(frame);
            if (_railExpanded)
                card.Children.Add(new TextBlock
                {
                    Text = $"{m.Index + 1} · {m.Width}×{m.Height}",
                    Foreground = new SolidColorBrush(Res("MutedBrush")), FontSize = 10.5,
                    Margin = new Thickness(2, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                });
            RailList.Children.Add(card);
        }
        UpdateMinimap();
    }

    private void SwitchTo(int index)
    {
        if (!_connected) return;
        CloseOverview();
        _newWin.Remove(index);        // viewing it clears the flag
        if (index == _currentMon) { RebuildRail(); return; }
        _currentMon = index;
        _session?.SelectMonitor(index);
        RebuildMonitorButtons(); RebuildRail(); SetZoom(0);
    }

    private void UpdateMinimap()
    {
        if (_miniRect == null || _miniCanvas == null || _miniIndex != _currentMon) return;
        if (_zoom <= 0 || _srcW <= 0 || _srcH <= 0) { _miniRect.Visibility = Visibility.Collapsed; return; }
        double cw = _miniCanvas.Width, ch = _miniCanvas.Height;
        double sx = cw / _srcW, sy = ch / _srcH;
        double visW = Math.Min(_srcW, Scroller.ViewportWidth / _zoom);
        double visH = Math.Min(_srcH, Scroller.ViewportHeight / _zoom);
        _miniRect.Visibility = Visibility.Visible;
        _miniRect.Width = Math.Max(6, visW * sx);
        _miniRect.Height = Math.Max(6, visH * sy);
        Canvas.SetLeft(_miniRect, Scroller.HorizontalOffset / _zoom * sx);
        Canvas.SetTop(_miniRect, Scroller.VerticalOffset / _zoom * sy);
    }

    private void MiniDown(object sender, MouseButtonEventArgs e)
    { _miniDragging = true; _miniCanvas?.CaptureMouse(); MiniTo(e.GetPosition(_miniCanvas)); e.Handled = true; }
    private void MiniMove(object sender, MouseEventArgs e)
    { if (_miniDragging) { MiniTo(e.GetPosition(_miniCanvas)); e.Handled = true; } }
    private void MiniUp(object sender, MouseButtonEventArgs e)
    { _miniDragging = false; _miniCanvas?.ReleaseMouseCapture(); e.Handled = true; }

    private void MiniTo(Point p)
    {
        if (_miniCanvas == null || _zoom <= 0) return;
        double cw = _miniCanvas.Width, ch = _miniCanvas.Height;
        double srcX = p.X / cw * _srcW, srcY = p.Y / ch * _srcH;
        Scroller.ScrollToHorizontalOffset(srcX * _zoom - Scroller.ViewportWidth / 2);
        Scroller.ScrollToVerticalOffset(srcY * _zoom - Scroller.ViewportHeight / 2);
    }

    private void OverviewBtn_Click(object sender, RoutedEventArgs e)
    {
        BuildOverview();
        Overview.Visibility = Visibility.Visible;
    }

    private void OverviewClose_Click(object sender, RoutedEventArgs e) => CloseOverview();
    private void CloseOverview() { if (Overview != null) Overview.Visibility = Visibility.Collapsed; }

    private void BuildOverview()
    {
        OverviewGrid.Children.Clear(); _ovImg.Clear();
        OverviewTitle.Text = _monitors.Count > 1 ? $"All monitors · {_monitors.Count}" : "Display";
        double dispW = 280;
        foreach (var m in _monitors)
        {
            double dispH = Math.Max(60, dispW * m.Height / Math.Max(1, m.Width));
            bool active = m.Index == _currentMon;
            bool flagged = _newWin.Contains(m.Index);
            int idx = m.Index;

            var img = new Image { Stretch = Stretch.Fill };
            if (_thumbImg.TryGetValue(idx, out var src)) img.Source = src;
            _ovImg[idx] = img;

            var frame = new Border
            {
                Width = dispW, Height = dispH, CornerRadius = new CornerRadius(10), ClipToBounds = true,
                BorderThickness = new Thickness(active || flagged ? 3 : 1),
                BorderBrush = new SolidColorBrush(active ? Color.FromRgb(0x3E, 0xC8, 0xFF)
                                                : flagged ? Color.FromRgb(0xFF, 0xAA, 0x1D) : Res("LineBrush")),
                Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x11, 0x17)),
                Child = img, Cursor = Cursors.Hand
            };
            frame.MouseLeftButtonUp += (_, _) => SwitchTo(idx);

            var card = new StackPanel { Margin = new Thickness(0, 0, 16, 16), Width = dispW };
            card.Children.Add(frame);
            string tag = flagged ? "  ● New window" : active ? "  (viewing)" : "";
            card.Children.Add(new TextBlock
            {
                Text = $"Display {m.Index + 1} · {m.Name} · {m.Width}×{m.Height}{tag}",
                Foreground = new SolidColorBrush(flagged ? Color.FromRgb(0xFF, 0xAA, 0x1D)
                                               : active ? Color.FromRgb(0x3E, 0xC8, 0xFF) : Res("MutedBrush")),
                FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            OverviewGrid.Children.Add(card);
        }
    }

    // ---- file transfer tray + docked chip (Phase D) ----
    private sealed class TransferRow
    {
        public bool Up; public string Name = ""; public long Done, Total, Prev; public double Speed;
        public bool Completed, Failed; public double TrackWidth = 300;
        public string? LocalPath;   // where the file is on THIS PC (upload source / download target)
        public System.Windows.Shapes.Rectangle Bar = null!; public TextBlock Sub = null!; public Border Card = null!;
        public Button? Folder;
    }

    private readonly Dictionary<string, TransferRow> _tx = new();
    private readonly Dictionary<string, string> _uploadPaths = new();   // filename → local source path
    private System.Windows.Threading.DispatcherTimer? _txTimer;
    private bool _trayClosed;

    private static string TxKey(string name, bool up) => (up ? "U:" : "D:") + name;

    private void EnsureTrayVisible()
    {
        if (_trayClosed) return;
        Tray.Visibility = Visibility.Visible; TrayChip.Visibility = Visibility.Collapsed;
        if (_txTimer == null)
        {
            _txTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _txTimer.Tick += TxTick; _txTimer.Start();
        }
    }

    private void TxProgress(string name, bool up, long done, long total)
    {
        _trayClosed = false;                 // a new/continuing transfer re-opens the tray
        EnsureTrayVisible();
        if (!_tx.TryGetValue(TxKey(name, up), out var row)) { row = CreateRow(name, up); _tx[TxKey(name, up)] = row; }
        row.Total = Math.Max(total, done); row.Done = done;
        if (row.Total > 0 && done >= row.Total) { row.Completed = true; row.Speed = 0; }
        UpdateRow(row);
        UpdateChip();
    }

    private void TxComplete(string name, bool up)
    {
        if (_tx.TryGetValue(TxKey(name, up), out var row) && !row.Completed) { row.Completed = true; row.Speed = 0; UpdateRow(row); }
        UpdateChip();
    }

    private void TxError(string _)
    {
        var row = _tx.Values.LastOrDefault(r => !r.Completed && !r.Failed);
        if (row != null) { row.Failed = true; UpdateRow(row); }
        UpdateChip();
    }

    private TransferRow CreateRow(string name, bool up)
    {
        var row = new TransferRow { Up = up, Name = name };
        var title = new TextBlock
        {
            Text = $"{(up ? "↑" : "↓")}  {name}", Foreground = new SolidColorBrush(Res("TextBrush")),
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (up && _uploadPaths.TryGetValue(name, out var up0)) row.LocalPath = up0;
        var folder = new Button
        {
            Content = "📁", Width = 26, Height = 22, Cursor = Cursors.Hand, ToolTip = "Open containing folder",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Res("MutedBrush")),
            Visibility = row.LocalPath != null ? Visibility.Visible : Visibility.Collapsed
        };
        folder.Click += (_, _) => RevealInExplorer(row.LocalPath);
        row.Folder = folder;
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0); Grid.SetColumn(folder, 1);
        titleRow.Children.Add(title); titleRow.Children.Add(folder);
        var route = new TextBlock
        {
            Text = up ? $"This PC → {_display}" : $"{_display} → This PC",
            Foreground = new SolidColorBrush(Res("MutedBrush")), FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 5)
        };
        var bar = new System.Windows.Shapes.Rectangle { Height = 6, Width = 0, RadiusX = 3, RadiusY = 3, Fill = (Brush)Resources["GradBrush"] };
        var track = new Border
        {
            Width = row.TrackWidth, Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            Background = new SolidColorBrush(Res("CardBrush")), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { bar } }
        };
        var sub = new TextBlock { Text = "", Foreground = new SolidColorBrush(Res("MutedBrush")), FontSize = 10.5, Margin = new Thickness(0, 5, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(titleRow); stack.Children.Add(route); stack.Children.Add(track); stack.Children.Add(sub);
        row.Card = new Border { Margin = new Thickness(0, 0, 0, 12), Child = stack };
        row.Bar = bar; row.Sub = sub;
        TrayList.Children.Add(row.Card);
        return row;
    }

    private void UpdateRow(TransferRow row)
    {
        double pct = row.Total > 0 ? Math.Min(1.0, (double)row.Done / row.Total) : (row.Completed ? 1 : 0);
        row.Bar.Width = row.TrackWidth * pct;
        if (row.Failed)
        {
            row.Sub.Text = "✕ failed"; row.Sub.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x48, 0x4D));
            row.Bar.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0x48, 0x4D)); return;
        }
        if (row.Completed)
        {
            row.Sub.Text = $"✓ verified · {Human(row.Total)}"; row.Sub.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0xC9, 0x8B));
            row.Bar.Fill = new SolidColorBrush(Color.FromRgb(0x1F, 0xC9, 0x8B)); return;
        }
        string speed = row.Speed > 0 ? $" · {Human((long)row.Speed)}/s" : "";
        string eta = row.Speed > 0 && row.Total > row.Done ? " · " + Eta((row.Total - row.Done) / row.Speed) : "";
        row.Sub.Text = $"{Human(row.Done)} / {Human(row.Total)}{speed}{eta}";
    }

    private void TxTick(object? sender, EventArgs e)
    {
        foreach (var r in _tx.Values)
            if (!r.Completed && !r.Failed) { r.Speed = Math.Max(0, r.Done - r.Prev); r.Prev = r.Done; UpdateRow(r); }
        UpdateChip();
    }

    private void UpdateChip()
    {
        long done = 0, total = 0; int active = 0;
        foreach (var r in _tx.Values) { done += r.Done; total += Math.Max(r.Total, r.Done); if (!r.Completed && !r.Failed) active++; }
        int pct = total > 0 ? (int)(done * 100 / total) : 0;
        ChipPct.Text = $"{pct}%";
        ChipCount.Text = active > 0 ? $"{active} active" : "done";
        ChipPct.Foreground = new SolidColorBrush(active > 0 ? Res("AccentBrush") : Color.FromRgb(0x1F, 0xC9, 0x8B));
    }

    private void SetRowLocalPath(string name, bool up, string path)
    {
        if (_tx.TryGetValue(TxKey(name, up), out var row))
        {
            row.LocalPath = path;
            if (row.Folder != null) row.Folder.Visibility = Visibility.Visible;
        }
    }

    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Couldn't open folder: " + ex.Message); }
    }

    private bool AnyActive() => _tx.Values.Any(r => !r.Completed && !r.Failed);

    private void TrayMin_Click(object sender, RoutedEventArgs e)
    {
        Tray.Visibility = Visibility.Collapsed;
        TrayChip.Visibility = _tx.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateChip();
    }

    private void TrayClose_Click(object sender, RoutedEventArgs e)
    {
        if (AnyActive()) { TrayMin_Click(sender, e); return; }   // invariant: active transfers never invisible
        Tray.Visibility = Visibility.Collapsed; TrayChip.Visibility = Visibility.Collapsed;
        TrayList.Children.Clear(); _tx.Clear(); _trayClosed = true;
    }

    private void TrayChip_Click(object sender, MouseButtonEventArgs e)
    {
        _trayClosed = false; Tray.Visibility = Visibility.Visible; TrayChip.Visibility = Visibility.Collapsed;
    }

    private static string Human(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }

    private static string Eta(double sec)
    {
        if (sec < 1) return "<1s";
        int s = (int)sec;
        return s < 60 ? $"{s}s" : $"{s / 60}:{s % 60:00}";
    }

    // ---- frame rendering ----
    private WriteableBitmap? _wb;
    private void DrawFrame(int w, int h, byte[] bgr)
    {
        if (w <= 0 || h <= 0 || bgr.Length < w * h * 3) return;
        Dispatcher.Invoke(() =>
        {
            bool sizeChanged = _srcW != w || _srcH != h;
            _srcW = w; _srcH = h;
            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                RemoteImage.Source = _wb;
            }
            _wb.WritePixels(new Int32Rect(0, 0, w, h), bgr, w * 3, 0);
            if (sizeChanged) UpdateReadout();
        });
    }

    // ---- input ----
    private (double x, double y) Norm(Point p)
    {
        double cw = RemoteImage.ActualWidth, ch = RemoteImage.ActualHeight;
        if (cw <= 0 || ch <= 0) return (0, 0);
        double scale = Math.Min(cw / _srcW, ch / _srcH);
        double drawW = _srcW * scale, drawH = _srcH * scale;
        double offX = (cw - drawW) / 2, offY = (ch - drawH) / 2;
        double nx = (p.X - offX) / drawW, ny = (p.Y - offY) / drawH;
        return (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
    }

    private void RemoteImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_connected) return;
        if (_zoom > 0) UpdateEdgePan(e);
        long now = _moveClock.ElapsedMilliseconds;
        if (now - _lastMoveMs < 12) return;
        _lastMoveMs = now;
        var (x, y) = Norm(e.GetPosition(RemoteImage));
        _session!.MouseMove(x, y);
    }

    private void RemoteImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_connected) return;
        RemoteImage.Focus();
        var (x, y) = Norm(e.GetPosition(RemoteImage));
        _session!.MouseButton(x, y, ToBtn(e.ChangedButton), true);
    }

    private void RemoteImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_connected) return;
        var (x, y) = Norm(e.GetPosition(RemoteImage));
        _session!.MouseButton(x, y, ToBtn(e.ChangedButton), false);
    }

    private static int ToBtn(MouseButton b) => b switch
    {
        MouseButton.Left => 0, MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0
    };

    // ---- file transfer ----
    private RemoteBrowserWindow? _browser;
    private void TransferBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        if (_browser is { IsLoaded: true }) { _browser.Activate(); return; }
        _browser = new RemoteBrowserWindow(session, _display, _id) { Owner = this };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
    }

    private void ClipboardBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        try
        {
            string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (string.IsNullOrEmpty(text)) { SetStatus("Your clipboard has no text to send."); return; }
            _session!.SendClipboard(text);
            SetStatus($"Sent {text.Length} chars to the remote clipboard ✓");
        }
        catch (Exception ex) { SetStatus("Clipboard read failed: " + ex.Message, "#F5484D"); }
        RemoteImage.Focus();
    }

    private void WinKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        const int VK_LWIN = 0x5B;
        _session!.KeyVirtual(VK_LWIN, true);
        _session!.KeyVirtual(VK_LWIN, false);
        RemoteImage.Focus();
    }

    private void FileProgress(string verb, string name, long done, long total)
    {
        int pct = total > 0 ? (int)(done * 100 / total) : 100;
        if (pct == _lastFilePct) return;
        _lastFilePct = pct;
        Dispatcher.BeginInvoke(() => SetStatus($"{verb} {name}… {pct}%"));
    }

    // ---- drag-drop upload ----
    private void RemoteView_DragOver(object sender, DragEventArgs e)
    {
        bool ok = _connected && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void RemoteView_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void RemoteView_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!_connected || _session is not { } session) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(File.Exists).ToArray();
        if (files.Length == 0) { SetStatus("Only files can be dropped (folders aren't supported yet)."); return; }
        _lastFilePct = -1;
        int sent = 0;
        foreach (var f in files)
        {
            _uploadPaths[Path.GetFileName(f)] = f;
            try { await session.Files.SendFileAsync(f, FileTransferChannel.CurrentFolderToken); sent++; }
            catch (Exception ex) { SetStatus($"Send failed on {Path.GetFileName(f)}: {ex.Message}", "#F5484D"); return; }
        }
        _lastFilePct = -1;
        SetStatus(files.Length == 1
            ? $"Sent {Path.GetFileName(files[0])} → remote open folder ✓"
            : $"Sent {sent} files → remote open folder ✓");
    }

    // ---- keyboard ----
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Overview.Visibility == Visibility.Visible) { CloseOverview(); e.Handled = true; return; }
        if (ForwardKey(e, down: true)) e.Handled = true; else base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (ForwardKey(e, down: false)) e.Handled = true; else base.OnPreviewKeyUp(e);
    }

    private bool ForwardKey(KeyEventArgs e, bool down)
    {
        if (!_connected) return false;
        if (Keyboard.FocusedElement is TextBox or ComboBox) return false;
        // Ctrl+Alt+End closes the session locally.
        if (down && e.Key == Key.End &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            Close();
            return true;
        }
        // Ctrl+V of copied FILES → transfer them into the remote's open folder instead of
        // forwarding the keystroke (text paste falls through and pastes the synced clipboard).
        if (down && e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToArray();
                    if (files.Length > 0) { _ = PasteFilesAsync(files); return true; }
                }
            }
            catch { /* fall through to a normal keystroke */ }
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;
        _session!.KeyVirtual(vk, down);
        return true;
    }

    private async Task PasteFilesAsync(string[] files)
    {
        if (_session is not { } session) return;
        SetStatus(files.Length == 1 ? $"Pasting {Path.GetFileName(files[0])}…" : $"Pasting {files.Length} files…");
        int sent = 0;
        foreach (var f in files)
        {
            _uploadPaths[Path.GetFileName(f)] = f;
            try { await session.Files.SendFileAsync(f, FileTransferChannel.CurrentFolderToken); sent++; }
            catch (Exception ex) { SetStatus($"Paste failed on {Path.GetFileName(f)}: {ex.Message}", "#F5484D"); return; }
        }
        SetStatus(sent == 1 ? $"Pasted {Path.GetFileName(files[0])} → remote open folder ✓" : $"Pasted {sent} files → remote open folder ✓");
    }

    protected override async void OnClosed(EventArgs e)
    {
        _timerTick?.Stop();
        _pingTimer?.Stop();
        _txTimer?.Stop();
        if (_clipHooked) { try { RemoveClipboardFormatListener(_hwnd); } catch { } _clipHooked = false; }
        StopPan();
        if (_session != null) { try { await _session.CloseAsync(); } catch { } _session.Dispose(); _session = null; }
        base.OnClosed(e);
    }
}
