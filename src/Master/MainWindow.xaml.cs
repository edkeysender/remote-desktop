using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private ViewerSession? _session;
    private bool _connected;
    private int _srcW = 1, _srcH = 1;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private long _lastMoveMs;

    private List<RemoteMonitor> _monitors = new();
    private int _currentMon;
    private double _zoom;               // 0 = fit-to-window, else pixel scale factor
    private int _lastFilePct = -1;      // throttle file progress updates to whole percents

    // Edge-pan: a capped velocity applied on a timer, so pan speed is independent of how
    // often the mouse moves (previously it scrolled per MouseMove event → far too fast).
    private double _panVx, _panVy;      // px/second
    private System.Windows.Threading.DispatcherTimer? _panTimer;
    private readonly Stopwatch _panClock = new();

    private static readonly Color Accent = Color.FromRgb(0x1f, 0x6f, 0xeb);
    private static readonly Color AccentBorder = Color.FromRgb(0x58, 0xa6, 0xff);
    private static readonly Color IdleBg = Color.FromRgb(0x0d, 0x11, 0x17);
    private static readonly Color IdleBorder = Color.FromRgb(0x30, 0x36, 0x3d);
    private static readonly Color IdleFg = Color.FromRgb(0x8b, 0x94, 0x9e);

    private readonly Updater _updater = new("master", elevate: false);
    private UpdateInfo? _pendingUpdate;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("master");
        ServerBox.Text = _config.ServerUrl;
        Loaded += (_, _) => StartUpdateChecks();
    }

    // Check on launch, then every 5 minutes, so the button appears while the app is
    // open (no relaunch needed). Stops polling once an update has been found.
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
        if (_connected)
        {
            SetStatus("Disconnect before updating.");
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

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { await DisconnectAsync(); return; }
        await StartSessionAsync(IdBox.Text.Trim().Replace(" ", ""), PwBox.Text.Trim(), adminPw: null);
    }

    // Core connect path. adminPw != null → password-less admin connect (directory picker);
    // otherwise the typed per-host password is used.
    private async Task StartSessionAsync(string id, string pw, string? adminPw)
    {
        if (_connected) return;
        _config.ServerUrl = ServerBox.Text.Trim();
        _config.Save("master");
        if (id.Length == 0) { SetStatus("Enter an ID."); return; }

        _session = new ViewerSession();
        _session.Connected += () => Dispatcher.Invoke(OnConnected);
        _session.Rejected  += r  => Dispatcher.Invoke(() => { SetStatus("Rejected: " + r); Cleanup(); });
        _session.Frame     += DrawFrame;   // decoded BGR frames from the WebRTC track
        _session.Monitors  += (mons, cur) => Dispatcher.Invoke(() => PopulateMonitors(mons, cur));
        _session.Closed    += r  => Dispatcher.Invoke(() => { if (_connected) SetStatus("Closed: " + (r ?? "")); Cleanup(); });

        // File transfer feedback (events fire on data-channel threads).
        _session.Files.SendProgress     += (n, done, total) => FileProgress("Sending", n, done, total);
        _session.Files.ReceiveStarted   += (n, _) => Dispatcher.Invoke(() => SetStatus($"Receiving {n}…"));
        _session.Files.ReceiveProgress  += (n, done, total) => FileProgress("Receiving", n, done, total);
        _session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() =>
            { _lastFilePct = -1; SetStatus($"Received {n} → {Path.GetDirectoryName(path)}"); });
        _session.Files.Error            += msg => Dispatcher.Invoke(() => SetStatus("File transfer failed: " + msg));

        try
        {
            SetStatus("Connecting…");
            await _session.ConnectAsync(_config.ServerUrl, id, pw, adminPw);
        }
        catch (Exception ex)
        {
            SetStatus("Connect failed: " + ex.Message);
            Cleanup();
        }
    }

    private void OnConnected()
    {
        _connected = true;
        ConnectBtn.Content = "Disconnect";
        ConnectBtn.Background = new SolidColorBrush(Color.FromRgb(0xda, 0x36, 0x33));
        SetStatus("● connected");
        Hint.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        ZoomBox.IsEnabled = true;
        TransferBtn.IsEnabled = true;
        WinKeyBtn.IsEnabled = true;
        RemoteImage.Focus();
        foreach (var c in new UIElement[] { ServerBox, IdBox, PwBox, DirectoryBtn }) c.IsEnabled = false;
    }

    private async void DirectoryBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0) { SetStatus("Enter the server URL first."); return; }
        var dlg = new DirectoryWindow(url, _config) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedId is not { } id) return;

        // Picked from the directory → connect straight away with the admin/directory
        // password, no per-host password prompt.
        IdBox.Text = id;
        if (_connected) await DisconnectAsync();
        await StartSessionAsync(id, pw: "", adminPw: _config.DirectoryPassword);
    }

    private async Task DisconnectAsync()
    {
        if (_session != null) await _session.CloseAsync();
        Cleanup();
        SetStatus("Disconnected");
    }

    private void Cleanup()
    {
        _connected = false;
        StopPan();
        _session?.Dispose();
        _session = null;
        ConnectBtn.Content = "Connect";
        ConnectBtn.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36));
        Scroller.Visibility = Visibility.Collapsed;
        RemoteImage.Source = null;
        _wb = null;
        Hint.Visibility = Visibility.Visible;
        MonitorPanel.Children.Clear();
        _monitors.Clear();
        ZoomBox.IsEnabled = false;
        TransferBtn.IsEnabled = false;
        WinKeyBtn.IsEnabled = false;
        _browser?.Close();
        _browser = null;
        _lastFilePct = -1;
        foreach (var c in new UIElement[] { ServerBox, IdBox, PwBox, DirectoryBtn }) c.IsEnabled = true;
    }

    private void SetStatus(string s) => StatusText.Text = s;

    // ---- monitor picker: numbered screen icons, selected one highlighted ----

    private void PopulateMonitors(List<RemoteMonitor> mons, int current)
    {
        _monitors = mons;
        _currentMon = current;
        RebuildMonitorButtons();
    }

    private void RebuildMonitorButtons()
    {
        MonitorPanel.Children.Clear();
        // "All" only makes sense with more than one display; a single display still
        // gets its icon so you can see what's selected.
        if (_monitors.Count > 1)
            MonitorPanel.Children.Add(MakeMonButton(-1, "All", "Show all monitors side by side"));
        foreach (var m in _monitors)
            MonitorPanel.Children.Add(MakeMonButton(m.Index, (m.Index + 1).ToString(),
                $"Display {m.Index + 1} — {m.Width}×{m.Height}{(m.Primary ? " (primary)" : "")}"));
    }

    private Button MakeMonButton(int index, string label, string tooltip)
    {
        var btn = new Button
        {
            Content = label,
            Tag = index,
            ToolTip = tooltip,
            Style = (Style)FindResource("MonBtn"),
        };
        bool selected = index == _currentMon;
        btn.Background = new SolidColorBrush(selected ? Accent : IdleBg);
        btn.BorderBrush = new SolidColorBrush(selected ? AccentBorder : IdleBorder);
        btn.Foreground = selected ? Brushes.White : new SolidColorBrush(IdleFg);
        btn.Click += (_, _) =>
        {
            if (!_connected || index == _currentMon) return;
            _currentMon = index;
            _session?.SelectMonitor(index);
            RebuildMonitorButtons();       // re-render selection immediately
        };
        return btn;
    }

    // ---- zoom: Fit scales down to the window; pixel modes pan a native-size image ----

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is not ComboBoxItem it || it.Tag is not string tag) return;
        _zoom = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        ApplyZoom();
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
            RemoteImage.Stretch = Stretch.None;   // native pixel size…
            RemoteImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);   // …times zoom
        }
    }

    /// <summary>Ctrl+wheel steps the zoom; plain wheel goes to the remote machine.</summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;   // never scroll locally — panning is edge-pan/scrollbars
        if (!_connected) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int step = e.Delta > 0 ? 1 : -1;
            int next = Math.Clamp(ZoomBox.SelectedIndex + step, 0, ZoomBox.Items.Count - 1);
            ZoomBox.SelectedIndex = next;
            return;
        }
        _session!.MouseWheel(e.Delta > 0 ? 120 : -120);
    }

    // ---- frame rendering: blit decoded BGR24 into a reused WriteableBitmap ----
    private WriteableBitmap? _wb;
    private void DrawFrame(int w, int h, byte[] bgr)
    {
        if (w <= 0 || h <= 0 || bgr.Length < w * h * 3) return;
        Dispatcher.Invoke(() =>
        {
            _srcW = w; _srcH = h;
            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                RemoteImage.Source = _wb;
            }
            _wb.WritePixels(new Int32Rect(0, 0, w, h), bgr, w * 3, 0);
        });
    }

    // ---- input capture ----
    // Map a pointer position on the letterboxed Image to normalized 0..1 of the source.
    // Positions from GetPosition(RemoteImage) are in the image's own (pre-LayoutTransform)
    // space, so this works identically in Fit and pixel-zoom modes.
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
        if (now - _lastMoveMs < 12) return;   // ~80 Hz
        _lastMoveMs = now;
        var (x, y) = Norm(e.GetPosition(RemoteImage));
        _session!.MouseMove(x, y);
    }

    // Max edge-pan speed in pixels/second at the very edge of the viewport; it eases in
    // from zero across a band so nudging the edge pans gently.
    private const double PanMaxSpeed = 650;
    private const double PanBand = 60;

    /// <summary>
    /// When zoomed past the window, set the pan velocity from how far the pointer is into
    /// the edge band. A timer applies it, so speed doesn't depend on mouse-move frequency.
    /// </summary>
    private void UpdateEdgePan(MouseEventArgs e)
    {
        var p = e.GetPosition(Scroller);
        double vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0) { StopPan(); return; }

        double fx = 0, fy = 0;   // -1..1 fraction into the band
        if (p.X < PanBand) fx = -(PanBand - p.X) / PanBand;
        else if (p.X > vw - PanBand) fx = (p.X - (vw - PanBand)) / PanBand;
        if (p.Y < PanBand) fy = -(PanBand - p.Y) / PanBand;
        else if (p.Y > vh - PanBand) fy = (p.Y - (vh - PanBand)) / PanBand;

        _panVx = Math.Clamp(fx, -1, 1) * PanMaxSpeed;
        _panVy = Math.Clamp(fy, -1, 1) * PanMaxSpeed;

        if (_panVx == 0 && _panVy == 0) return;   // let the timer coast to a stop
        if (_panTimer == null)
        {
            _panClock.Restart();
            _panTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _panTimer.Tick += PanTick;
            _panTimer.Start();
        }
    }

    private void PanTick(object? sender, EventArgs e)
    {
        double dt = _panClock.Elapsed.TotalSeconds;
        _panClock.Restart();
        if (_panVx == 0 && _panVy == 0) { StopPan(); return; }
        if (_panVx != 0) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + _panVx * dt);
        if (_panVy != 0) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + _panVy * dt);
    }

    private void StopPan()
    {
        _panVx = _panVy = 0;
        _panTimer?.Stop();
        _panTimer = null;
    }

    private void Scroller_MouseLeave(object sender, MouseEventArgs e) => StopPan();

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

    // ---- file transfer: open the remote file browser (download + upload) ----

    private RemoteBrowserWindow? _browser;

    private void TransferBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        if (_browser is { IsLoaded: true }) { _browser.Activate(); return; }
        _browser = new RemoteBrowserWindow(session) { Owner = this };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
    }

    // ---- Windows (Start) key: tap ⊞ on the remote PC ----
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
        if (pct == _lastFilePct) return;   // avoid flooding the dispatcher per 16 KB chunk
        _lastFilePct = pct;
        Dispatcher.BeginInvoke(() => SetStatus($"{verb} {name}… {pct}%"));
    }

    // ---- drag files from Explorer onto the remote view → auto-send to the client ----

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
            try
            {
                await session.Files.SendFileAsync(f);   // lands in the remote's Downloads
                sent++;
            }
            catch (Exception ex) { SetStatus($"Send failed on {Path.GetFileName(f)}: {ex.Message}"); return; }
        }
        _lastFilePct = -1;
        SetStatus(files.Length == 1
            ? $"Sent {Path.GetFileName(files[0])} → remote Downloads ✓"
            : $"Sent {sent} files → remote Downloads ✓");
    }

    // Keyboard is captured at the window level so Tab/arrows/modifiers are intercepted.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (ForwardKey(e, down: true)) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (ForwardKey(e, down: false)) e.Handled = true;
        else base.OnPreviewKeyUp(e);
    }

    private bool ForwardKey(KeyEventArgs e, bool down)
    {
        if (!_connected) return false;
        // Don't steal typing from our own toolbar controls (ID/password/server boxes).
        if (Keyboard.FocusedElement is TextBox or ComboBox) return false;
        // Local escape hatch: Ctrl+Alt+End releases control without touching the remote.
        if (down && e.Key == Key.End &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            _ = DisconnectAsync();
            return true;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;
        _session!.KeyVirtual(vk, down);
        return true;
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_session != null) await _session.CloseAsync();
        base.OnClosed(e);
    }
}
