using System.Diagnostics;
using System.IO;
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

    private static readonly Color Accent = Color.FromRgb(0x1f, 0x6f, 0xeb);
    private static readonly Color AccentBorder = Color.FromRgb(0x58, 0xa6, 0xff);
    private static readonly Color IdleBg = Color.FromRgb(0x0d, 0x11, 0x17);
    private static readonly Color IdleBorder = Color.FromRgb(0x30, 0x36, 0x3d);
    private static readonly Color IdleFg = Color.FromRgb(0x8b, 0x94, 0x9e);

    private readonly Updater _updater = new("master", elevate: false);
    private UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("master");
        ServerBox.Text = _config.ServerUrl;
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

        _config.ServerUrl = ServerBox.Text.Trim();
        _config.Save("master");

        var id = IdBox.Text.Trim().Replace(" ", "");
        var pw = PwBox.Text.Trim();
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
            await _session.ConnectAsync(_config.ServerUrl, id, pw);
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
        SendFileBtn.IsEnabled = true;
        RemoteImage.Focus();
        foreach (var c in new UIElement[] { ServerBox, IdBox, PwBox }) c.IsEnabled = false;
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
        SendFileBtn.IsEnabled = false;
        _lastFilePct = -1;
        foreach (var c in new UIElement[] { ServerBox, IdBox, PwBox }) c.IsEnabled = true;
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
        if (_zoom > 0) EdgePan(e);
        long now = _moveClock.ElapsedMilliseconds;
        if (now - _lastMoveMs < 12) return;   // ~80 Hz
        _lastMoveMs = now;
        var (x, y) = Norm(e.GetPosition(RemoteImage));
        _session!.MouseMove(x, y);
    }

    /// <summary>
    /// When zoomed past the window, pushing the pointer into a band near a viewport
    /// edge pans the view in that direction (speed grows toward the edge).
    /// </summary>
    private void EdgePan(MouseEventArgs e)
    {
        const double band = 48, maxStep = 26;
        var p = e.GetPosition(Scroller);
        double vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0) return;

        double dx = 0, dy = 0;
        if (p.X < band) dx = -(band - p.X);
        else if (p.X > vw - band) dx = p.X - (vw - band);
        if (p.Y < band) dy = -(band - p.Y);
        else if (p.Y > vh - band) dy = p.Y - (vh - band);

        if (dx != 0) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + Math.Clamp(dx / band, -1, 1) * maxStep);
        if (dy != 0) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + Math.Clamp(dy / band, -1, 1) * maxStep);
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

    private async void SendFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send a file to the remote machine" };
        if (dlg.ShowDialog(this) != true) return;

        SendFileBtn.IsEnabled = false;
        _lastFilePct = -1;
        try
        {
            await session.Files.SendFileAsync(dlg.FileName);
            SetStatus($"Sent {Path.GetFileName(dlg.FileName)} ✓ (saved to remote Downloads)");
        }
        catch (Exception ex)
        {
            SetStatus("Send failed: " + ex.Message);
        }
        finally
        {
            _lastFilePct = -1;
            SendFileBtn.IsEnabled = _connected;
        }
    }

    private void FileProgress(string verb, string name, long done, long total)
    {
        int pct = total > 0 ? (int)(done * 100 / total) : 100;
        if (pct == _lastFilePct) return;   // avoid flooding the dispatcher per 16 KB chunk
        _lastFilePct = pct;
        Dispatcher.BeginInvoke(() => SetStatus($"{verb} {name}… {pct}%"));
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
