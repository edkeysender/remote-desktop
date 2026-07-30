using System.Diagnostics;
using System.IO;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("master");
        ServerBox.Text = _config.ServerUrl;
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { await DisconnectAsync(); return; }

        _config.ServerUrl = ServerBox.Text.Trim();
        _config.Save("master");

        var id = IdBox.Text.Trim();
        var pw = PwBox.Text.Trim();
        if (id.Length == 0) { SetStatus("Enter an ID."); return; }

        _session = new ViewerSession();
        _session.Connected  += () => Dispatcher.Invoke(OnConnected);
        _session.Rejected   += r  => Dispatcher.Invoke(() => { SetStatus("Rejected: " + r); Cleanup(); });
        _session.ScreenSize += (w, h) => Dispatcher.Invoke(() => { _srcW = w; _srcH = h; });
        _session.Frame      += DrawFrame;   // decode off the UI thread
        _session.Closed     += r  => Dispatcher.Invoke(() => { if (_connected) SetStatus("Closed: " + (r ?? "")); Cleanup(); });

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
        RemoteImage.Visibility = Visibility.Visible;
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
        RemoteImage.Visibility = Visibility.Collapsed;
        RemoteImage.Source = null;
        Hint.Visibility = Visibility.Visible;
        foreach (var c in new UIElement[] { ServerBox, IdBox, PwBox }) c.IsEnabled = true;
    }

    private void SetStatus(string s) => StatusText.Text = s;

    // ---- frame rendering: decode on the calling (receive-loop) thread, assign on UI ----
    private void DrawFrame(byte[] jpeg)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(jpeg);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Dispatcher.Invoke(() => RemoteImage.Source = bmp);
        }
        catch { /* skip a corrupt frame */ }
    }

    // ---- input capture ----
    // Map a pointer position on the letterboxed Image to normalized 0..1 of the source.
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
        long now = _moveClock.ElapsedMilliseconds;
        if (now - _lastMoveMs < 12) return;   // ~80 Hz
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

    private void RemoteImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_connected) return;
        _session!.MouseWheel(e.Delta > 0 ? 120 : -120);
    }

    private static int ToBtn(MouseButton b) => b switch
    {
        MouseButton.Left => 0, MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0
    };

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
