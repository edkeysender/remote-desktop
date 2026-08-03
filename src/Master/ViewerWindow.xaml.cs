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
/// The chrome follows the Hangar design (Appendix D) — dark by default, light on toggle.
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
    private double _zoom;   // 0 = Fit, else scale factor
    private int _lastFilePct = -1;

    private double _panVx, _panVy;
    private System.Windows.Threading.DispatcherTimer? _panTimer;
    private readonly Stopwatch _panClock = new();

    private bool _dark = true;
    private readonly Stopwatch _sessionClock = new();
    private System.Windows.Threading.DispatcherTimer? _timerTick;

    public ViewerWindow(string serverUrl, string id, string display,
                        string? password = null, string? adminPw = null, string? authToken = null)
    {
        InitializeComponent();
        _serverUrl = serverUrl; _id = id; _display = display;
        _password = password ?? ""; _adminPw = adminPw; _authToken = authToken;
        Title = $"Hangar — {display}";
        TitleText.Text = display;
        Loaded += async (_, _) => await StartSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        _session = new ViewerSession();
        _session.Connected += () => Dispatcher.Invoke(OnConnected);
        _session.Rejected  += r  => Dispatcher.Invoke(() => { SetStatus("Rejected: " + r, "#F5484D"); CloseSoon(); });
        _session.Frame     += DrawFrame;
        _session.Monitors  += (mons, cur) => Dispatcher.Invoke(() => PopulateMonitors(mons, cur));
        _session.Closed    += r  => Dispatcher.Invoke(() => { if (_connected) SetStatus("Closed: " + (r ?? ""), "#8A8DA3"); CloseSoon(); });

        _session.Files.SendProgress     += (n, done, total) => FileProgress("Sending", n, done, total);
        _session.Files.ReceiveStarted   += (n, _) => Dispatcher.Invoke(() => SetStatus($"Receiving {n}…"));
        _session.Files.ReceiveProgress  += (n, done, total) => FileProgress("Receiving", n, done, total);
        _session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() =>
            { _lastFilePct = -1; SetStatus($"Received {n} → {Path.GetDirectoryName(path)}"); });
        _session.Files.Error            += msg => Dispatcher.Invoke(() => SetStatus("File transfer failed: " + msg, "#F5484D"));

        try
        {
            SetStatus("Connecting…");
            await _session.ConnectAsync(_serverUrl, _id, _password, _adminPw, _authToken);
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
        SetStatus("Connected", "#1FC98B");
        Hint.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        foreach (var b in new[] { ZoomFit, Zoom100, Zoom200, TransferBtn, WinKeyBtn }) b.IsEnabled = true;
        SetZoom(0);   // Fit

        // Live latency + verified P2P/relay state arrive in Phase F; show a neutral live state until then.
        P2pText.Text = "Live";

        _sessionClock.Restart();
        _timerTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timerTick.Tick += (_, _) =>
        {
            var e = _sessionClock.Elapsed;
            TimerText.Text = e.TotalHours >= 1 ? $"{(int)e.TotalHours}:{e.Minutes:00}:{e.Seconds:00}" : $"{e.Minutes:00}:{e.Seconds:00}";
        };
        _timerTick.Start();
        RemoteImage.Focus();
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string s, string? dotHex = null)
    {
        StatusText.Text = s;
        if (dotHex != null) StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dotHex));
    }

    // ---- monitor picker (moves into the left rail in Phase B) ----
    private void PopulateMonitors(List<RemoteMonitor> mons, int current)
    {
        _monitors = mons; _currentMon = current; RebuildMonitorButtons(); UpdateReadout();
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
    }

    // ---- theme ----
    private void ThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_dark);

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        void Set(string key, string hex) => ((SolidColorBrush)Resources[key]).Color = (Color)ColorConverter.ConvertFromString(hex);
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
        SetZoom(_zoom);   // refresh segment colours for the new theme
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
        _browser = new RemoteBrowserWindow(session) { Owner = this };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
    }

    private void ClipboardBtn_Click(object sender, RoutedEventArgs e)
        => SetStatus("Clipboard sync arrives in a later update.");

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
            catch (Exception ex) { SetStatus($"Send failed on {Path.GetFileName(f)}: {ex.Message}", "#F5484D"); return; }
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
        _timerTick?.Stop();
        StopPan();
        if (_session != null) { try { await _session.CloseAsync(); } catch { } _session.Dispose(); _session = null; }
        base.OnClosed(e);
    }
}
