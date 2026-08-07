using System.Security.Cryptography;
using System.Windows;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private HostSession? _session;

    // Always-on connection: a background loop keeps a HostSession alive and reconnects
    // if the relay drops, for as long as the window is open. Restarted when the server
    // URL or password changes.
    private CancellationTokenSource? _loopCts;
    private string _appliedServer = "";
    private string _appliedPw = "";

    private readonly Updater _updater = new("client", elevate: true);
    private UpdateInfo? _pendingUpdate;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load("client");

        // Stable per-device identity: a persistent token makes the relay hand back the
        // SAME ID on every launch (like the unattended worker). Password is persisted and
        // editable, so it too is fixed until the user changes it.
        if (string.IsNullOrEmpty(_config.HostToken)) _config.HostToken = Guid.NewGuid().ToString("N");
        if (string.IsNullOrEmpty(_config.FixedPassword)) _config.FixedPassword = GeneratePassword();
        _config.Save("client");

        ServerBox.Text = _config.ServerUrl;
        PwBox.Text = _config.FixedPassword;
        ShowUnattendedInfo();
        Loaded += (_, _) =>
        {
            StartUpdateChecks();
            ApplyAndConnect();            // connect automatically on launch
        };
    }

    // ---- always-on connection ----

    // Read the current server/password, persist them, and (re)start the connection loop
    // if they changed. Called on launch and whenever those fields are edited.
    private void ApplyAndConnect()
    {
        var server = ServerBox.Text.Trim();
        var pw = PwBox.Text.Trim();
        if (pw.Length == 0) { SetStatus("Set a password — it's required to allow connections."); return; }
        if (server == _appliedServer && pw == _appliedPw && _loopCts != null) return;   // nothing changed

        _config.ServerUrl = server;
        _config.FixedPassword = pw;       // persist the (editable) password so it's fixed
        _config.Save("client");
        _appliedServer = server;
        _appliedPw = pw;

        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        _ = RunConnectionLoopAsync(server, pw, _config.HostToken!, _loopCts.Token);
    }

    private async Task RunConnectionLoopAsync(string server, string pw, string token, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HostSession? session = null;
            var down = new TaskCompletionSource();
            try
            {
                session = new HostSession(server, pw, token: token);
                _session = session;
                session.IdAssigned    += id     => Dispatcher.Invoke(() => IdBox.Text = FormatId(id));
                session.Status        += s      => Dispatcher.Invoke(() => SetStatus(s));
                session.SessionActive += active => Dispatcher.Invoke(() => ShowBanner(active));
                session.ChatReceived  += text   => Dispatcher.Invoke(() => AddChatLine("Operator", text, mine: false));
                session.Files.ReceiveStarted   += (n, _)    => Dispatcher.Invoke(() => SetStatus($"Receiving {n}…"));
                session.Files.ReceiveCompleted += (n, path) => Dispatcher.Invoke(() => SetStatus($"Received {n} → {path}"));
                session.Files.Error            += msg       => Dispatcher.Invoke(() => SetStatus("File transfer failed: " + msg));
                // The signaling connection dropping surfaces as a "Disconnected" status.
                session.Status += s => { if (s.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase)) down.TrySetResult(); };

                await session.StartAsync();
                using (ct.Register(() => down.TrySetResult()))
                    await down.Task;           // block until the connection drops or we're cancelled
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus("Connection error — retrying… " + ex.Message));
            }
            finally
            {
                try { session?.Dispose(); } catch { }
                if (_session == session) _session = null;
            }

            if (ct.IsCancellationRequested) break;
            Dispatcher.Invoke(() => { ShowBanner(false); IdBox.Text = "reconnecting…"; });
            try { await Task.Delay(3000, ct); } catch { break; }   // reconnect backoff
        }
    }

    private void Settings_LostFocus(object sender, RoutedEventArgs e) => ApplyAndConnect();

    // ---- updates ----

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
            SetStatus($"Downloading v{info.Version}…");
            var progress = new Progress<double>(p => Dispatcher.Invoke(() => SetStatus($"Downloading v{info.Version}… {(int)(p * 100)}%")));
            var stage = await _updater.DownloadAsync(ServerBox.Text.Trim(), info, progress);
            SetStatus("Installing — the app will restart…");
            _loopCts?.Cancel();
            if (_session != null) { try { await _session.StopAsync(); } catch { } }
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

    private void ShowBanner(bool active)
    {
        Banner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // Chat is only meaningful while someone is connected; clear it between sessions.
        ChatPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) ChatList.Children.Clear();
    }

    // ---- chat with the operator ----
    private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        SendChatMessage();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendChatMessage();

    private void SendChatMessage()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        if (_session?.SendChat(text) == true) { AddChatLine("You", text, mine: true); ChatInput.Clear(); }
        else SetStatus("Chat unavailable — no operator is connected.");
    }

    private void AddChatLine(string who, string text, bool mine)
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(mine ? 28 : 0, 0, mine ? 0 : 28, 8),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"{who} · {DateTime.Now:HH:mm}",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e)),
            FontSize = 10.5,
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 300,
            Foreground = new System.Windows.Media.SolidColorBrush(mine
                ? System.Windows.Media.Color.FromRgb(0x9F, 0xE8, 0xFF)
                : System.Windows.Media.Color.FromRgb(0xe6, 0xed, 0xf3)),
        });
        ChatList.Children.Add(stack);
        ChatScroll.ScrollToEnd();
        if (!mine) SetStatus("New chat message from the operator");
    }

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
        _loopCts?.Cancel();
        if (_session != null) { try { await _session.StopAsync(); } catch { } }
        base.OnClosed(e);
    }
}
