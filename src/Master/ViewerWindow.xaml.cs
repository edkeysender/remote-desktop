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

/// <summary>
/// A single remote-control session in its own window. Opened by the hub with a target
/// (server + id) and one auth mode: an account token (password-less), an admin/directory
/// password, or a per-host password. Closing the window ends the session.
/// </summary>
public partial class ViewerWindow : Window
{
    private readonly string _serverUrl, _id, _password, _display;
    private readonly string? _adminPw, _authToken;

    private ViewerSession? _session;
    private bool _connected;
    private int _srcW = 1, _srcH = 1;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private long _lastMoveMs;

    private List<RemoteMonitor> _monitors = new();
    private int _currentMon;
    private double _zoom;
    private int _lastFilePct = -1;

    private double _panVx, _panVy;
    private System.Windows.Threading.DispatcherTimer? _panTimer;
    private readonly Stopwatch _panClock = new();

    private static readonly Color Accent = Color.FromRgb(0x1f, 0x6f, 0xeb);
    private static readonly Color AccentBorder = Color.FromRgb(0x58, 0xa6, 0xff);
    private static readonly Color IdleBg = Color.FromRgb(0x0d, 0x11, 0x17);
    private static readonly Color IdleBorder = Color.FromRgb(0x30, 0x36, 0x3d);
    private static readonly Color IdleFg = Color.FromRgb(0x8b, 0x94, 0x9e);

    public ViewerWindow(string serverUrl, string id, string display,
                        string? password = null, string? adminPw = null, string? authToken = null)
    {
        InitializeComponent();
        _serverUrl = serverUrl; _id = id; _display = display;
        _password = password ?? ""; _adminPw = adminPw; _authToken = authToken;
        Title = $"FTD Remote — {display}";
        TitleText.Text = display;
        Loaded += async (_, _) => await StartSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        _session = new ViewerSession();
        _session.Connected += () => Dispatcher.Invoke(OnConnected);
        _session.Rejected  += r  => Dispatcher.Invoke(() => { SetStatus("Rejected: " + r); CloseSoon(); });
        _session.Frame     += DrawFrame;
        _session.Monitors  += (mons, cur) => Dispatcher.Invoke(() => PopulateMonitors(mons, cur));
        _session.Closed    += r  => Dispatcher.Invoke(() => { if (_connected) SetStatus("Closed: " + (r ?? "")); CloseSoon(); });

        _session.Files.SendProgress     += (n, done, total) => FileProgress("Sending", n, done, total);
        _session.Files.ReceiveStarted   += (n, _) => Dispatcher.Invoke(() => SetStatus($"Receiving {n}…"));
        _session.Files.ReceiveProgress  += (n, done, total) => FileProgress("Receiving", n, done, total);
        _session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() =>
            { _lastFilePct = -1; SetStatus($"Received {n} → {Path.GetDirectoryName(path)}"); });
        _session.Files.Error            += msg => Dispatcher.Invoke(() => SetStatus("File transfer failed: " + msg));

        try
        {
            SetStatus("Connecting…");
            await _session.ConnectAsync(_serverUrl, _id, _password, _adminPw, _authToken);
        }
        catch (Exception ex)
        {
            SetStatus("Connect failed: " + ex.Message);
            CloseSoon();
        }
    }

    private bool _closing;
    private void CloseSoon()
    {
        if (_closing) return;
        _closing = true;
        // Give the user a moment to read a rejection/close reason, then close the window.
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }

    private void OnConnected()
    {
        _connected = true;
        SetStatus("● connected");
        Hint.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        ZoomBox.IsEnabled = true;
        TransferBtn.IsEnabled = true;
        WinKeyBtn.IsEnabled = true;
        RemoteImage.Focus();
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string s) => StatusText.Text = s;

    // ---- monitor picker ----
    private void PopulateMonitors(List<RemoteMonitor> mons, int current)
    {
        _monitors = mons; _currentMon = current; RebuildMonitorButtons();
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

    private Button MakeMonButton(int index, string label, string tooltip)
    {
        var btn = new Button { Content = label, Tag = index, ToolTip = tooltip, Style = (Style)FindResource("MonBtn") };
        bool selected = index == _currentMon;
        btn.Background = new SolidColorBrush(selected ? Accent : IdleBg);
        btn.BorderBrush = new SolidColorBrush(selected ? AccentBorder : IdleBorder);
        btn.Foreground = selected ? Brushes.White : new SolidColorBrush(IdleFg);
        btn.Click += (_, _) =>
        {
            if (!_connected || index == _currentMon) return;
            _currentMon = index;
            _session?.SelectMonitor(index);
            RebuildMonitorButtons();
        };
        return btn;
    }

    // ---- zoom + pan ----
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
            int step = e.Delta > 0 ? 1 : -1;
            ZoomBox.SelectedIndex = Math.Clamp(ZoomBox.SelectedIndex + step, 0, ZoomBox.Items.Count - 1);
            return;
        }
        _session!.MouseWheel(e.Delta > 0 ? 120 : -120);
    }

    private const double PanMaxSpeed = 650;
    private const double PanBand = 60;

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
        if (_panVx == 0 && _panVy == 0) return;
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
        double dt = _panClock.Elapsed.TotalSeconds; _panClock.Restart();
        if (_panVx == 0 && _panVy == 0) { StopPan(); return; }
        if (_panVx != 0) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + _panVx * dt);
        if (_panVy != 0) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + _panVy * dt);
    }

    private void StopPan() { _panVx = _panVy = 0; _panTimer?.Stop(); _panTimer = null; }
    private void Scroller_MouseLeave(object sender, MouseEventArgs e) => StopPan();

    // ---- frame rendering ----
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
        _browser = new RemoteBrowserWindow(session) { Owner = this };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
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
            try { await session.Files.SendFileAsync(f); sent++; }
            catch (Exception ex) { SetStatus($"Send failed on {Path.GetFileName(f)}: {ex.Message}"); return; }
        }
        _lastFilePct = -1;
        SetStatus(files.Length == 1
            ? $"Sent {Path.GetFileName(files[0])} → remote Downloads ✓"
            : $"Sent {sent} files → remote Downloads ✓");
    }

    // ---- keyboard ----
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;
        _session!.KeyVirtual(vk, down);
        return true;
    }

    protected override async void OnClosed(EventArgs e)
    {
        StopPan();
        if (_session != null) { try { await _session.CloseAsync(); } catch { } _session.Dispose(); _session = null; }
        base.OnClosed(e);
    }
}
