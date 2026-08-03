using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// Dual-pane file transfer (Appendix D): the local machine on the left, the remote machine
/// on the right, each browsable to its drives. Select a file and choose a direction —
/// Send (local → remote's current folder) or Get (remote → local's current folder).
/// Transfers run over the session's file data channel; progress also shows in the tray.
/// </summary>
public partial class RemoteBrowserWindow : Window
{
    // Public so WPF's GridView column bindings can read it (non-public → blank cells).
    public sealed record Row(string Name, string SizeText, bool IsDir, string FullPath);

    private readonly ViewerSession _session;
    private string _localCur = "";        // "" = drive list
    private string _remoteCur = "";       // "" = remote drive list
    private bool _busy, _gotRemote, _selGuard;
    private DispatcherTimer? _openTimer;

    public RemoteBrowserWindow(ViewerSession session, string remoteName, string remoteId)
    {
        InitializeComponent();
        _session = session;
        Subtitle.Text = $"This computer  ⇄  {remoteName}  ·  encrypted P2P channel";
        RemoteName.Text = remoteName;
        RemoteId.Text = FmtId(remoteId);
        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

        _session.Files.ListingReceived += OnListing;
        _session.Files.ListingError += OnListingError;
        _session.Files.ReceiveProgress += OnReceiveProgress;
        _session.Files.SendProgress += OnSendProgress;

        Loaded += (_, _) => { LocalNavigate(""); StartRemoteLoad(); };
        Closed += (_, _) =>
        {
            _openTimer?.Stop();
            _session.Files.ListingReceived -= OnListing;
            _session.Files.ListingError -= OnListingError;
            _session.Files.ReceiveProgress -= OnReceiveProgress;
            _session.Files.SendProgress -= OnSendProgress;
        };
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- local pane ----------------
    private void LocalNavigate(string path)
    {
        _localCur = path;
        var rows = new List<Row>();
        if (path.Length == 0)
        {
            LocalPath.Text = "This PC";
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady) rows.Add(new Row("📁 " + d.Name, "", true, d.Name));
        }
        else
        {
            LocalPath.Text = path;
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                    rows.Add(new Row("📁 " + Path.GetFileName(dir), "", true, dir));
                foreach (var f in Directory.GetFiles(path))
                {
                    long len = 0; try { len = new FileInfo(f).Length; } catch { }
                    rows.Add(new Row("📄 " + Path.GetFileName(f), HumanSize(len), false, f));
                }
            }
            catch (Exception ex) { StatusText.Text = "Can't open " + path + ": " + ex.Message; }
        }
        LocalList.ItemsSource = rows;
        LUp.IsEnabled = path.Length > 0;
        UpdateDirButtons();
    }

    private void LUp_Click(object sender, RoutedEventArgs e)
    {
        if (_localCur.Length == 0) return;
        LocalNavigate(Path.GetDirectoryName(_localCur.TrimEnd('\\', '/')) ?? "");
    }
    private void LRefresh_Click(object sender, RoutedEventArgs e) => LocalNavigate(_localCur);

    private void LocalList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not Row row) return;
        if (row.IsDir) LocalNavigate(row.FullPath);
        else Send_Click(sender, e);
    }

    // ---------------- remote pane ----------------
    private void StartRemoteLoad()
    {
        int ticks = 0;
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _openTimer.Tick += (_, _) =>
        {
            if (_gotRemote) { _openTimer!.Stop(); return; }
            if (_session.Files.IsOpen) _session.Files.RequestListing(_remoteCur);
            else if (++ticks > 30) { _openTimer!.Stop(); StatusText.Text = "File channel didn't open — try reconnecting."; }
        };
        _openTimer.Start();
        if (_session.Files.IsOpen) _session.Files.RequestListing(_remoteCur);
    }

    private void OnListing(FsListing listing) => Dispatcher.Invoke(() =>
    {
        _gotRemote = true;
        _remoteCur = listing.Path;
        RemotePath.Text = _remoteCur.Length == 0 ? "This PC" : _remoteCur;
        RUp.IsEnabled = _remoteCur.Length > 0;
        var rows = listing.Entries
            .OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new Row(
                (e.IsDir ? "📁 " : "📄 ") + e.Name,
                e.IsDir ? "" : HumanSize(e.Size),
                e.IsDir,
                _remoteCur.Length == 0 ? e.Name : Path.Combine(_remoteCur, e.Name)))
            .ToList();
        RemoteList.ItemsSource = rows;
        StatusText.Text = $"{rows.Count(r => r.IsDir)} folder(s), {rows.Count(r => !r.IsDir)} file(s)";
        UpdateDirButtons();
    });

    private void OnListingError(string path, string msg) =>
        Dispatcher.Invoke(() => StatusText.Text = $"Can't open {(path.Length == 0 ? "This PC" : path)}: {msg}");

    private void RUp_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteCur.Length == 0) return;
        _session.Files.RequestListing(Path.GetDirectoryName(_remoteCur.TrimEnd('\\', '/')) ?? "");
    }
    private void RRefresh_Click(object sender, RoutedEventArgs e) => _session.Files.RequestListing(_remoteCur);

    private void RemoteList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not Row row) return;
        if (row.IsDir) _session.Files.RequestListing(row.FullPath);
        else Get_Click(sender, e);
    }

    // ---------------- selection / direction ----------------
    private void Sel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selGuard) return;
        _selGuard = true;
        // A selection on one side clears the other, so the direction is always unambiguous.
        if (ReferenceEquals(sender, LocalList) && LocalList.SelectedItem != null) RemoteList.SelectedIndex = -1;
        else if (ReferenceEquals(sender, RemoteList) && RemoteList.SelectedItem != null) LocalList.SelectedIndex = -1;
        _selGuard = false;
        UpdateDirButtons();
    }

    private void UpdateDirButtons()
    {
        bool localFile = LocalList.SelectedItem is Row { IsDir: false };
        bool remoteFile = RemoteList.SelectedItem is Row { IsDir: false };
        bool open = _session.Files.IsOpen && !_busy;
        SendBtn.IsEnabled = localFile && open && _remoteCur.Length > 0;
        GetBtn.IsEnabled = remoteFile && open && _localCur.Length > 0;
        DirHint.Text = localFile ? (_remoteCur.Length == 0 ? "Open a remote folder to send into" : "Send this file to the remote folder")
                     : remoteFile ? (_localCur.Length == 0 ? "Open a local folder to save into" : "Get this file into your folder")
                     : "Select a file, then choose direction";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || LocalList.SelectedItem is not Row { IsDir: false } row) return;
        if (_remoteCur.Length == 0) { StatusText.Text = "Open a folder on the remote side first."; return; }
        SetBusy(true);
        try
        {
            await _session.Files.SendFileAsync(row.FullPath, destDir: _remoteCur);
            StatusText.Text = $"Sent {Path.GetFileName(row.FullPath)} → {_remoteCur}";
            _session.Files.RequestListing(_remoteCur);   // refresh remote so the new file shows
        }
        catch (Exception ex) { StatusText.Text = "Send failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Get_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RemoteList.SelectedItem is not Row { IsDir: false } row) return;
        if (_localCur.Length == 0) { StatusText.Text = "Open a folder on this computer first."; return; }
        SetBusy(true);
        try
        {
            var saved = await _session.Files.DownloadAsync(row.FullPath, Path.Combine(_localCur, Path.GetFileName(row.FullPath)));
            StatusText.Text = $"Got → {saved}";
            LocalNavigate(_localCur);
        }
        catch (Exception ex) { StatusText.Text = "Get failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private int _lastPct = -1;
    private void OnReceiveProgress(string n, long done, long total) => Progress("Getting", n, done, total);
    private void OnSendProgress(string n, long done, long total) => Progress("Sending", n, done, total);
    private void Progress(string verb, string name, long done, long total)
    {
        int pct = total > 0 ? (int)(done * 100 / total) : 100;
        if (pct == _lastPct) return;
        _lastPct = pct;
        Dispatcher.BeginInvoke(() => StatusText.Text = $"{verb} {name}… {pct}%");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _lastPct = -1;
        LocalList.IsEnabled = RemoteList.IsEnabled = !busy;
        LUp.IsEnabled = !busy && _localCur.Length > 0;
        RUp.IsEnabled = !busy && _remoteCur.Length > 0;
        LRefresh.IsEnabled = RRefresh.IsEnabled = !busy;
        UpdateDirButtons();
    }

    private static string FmtId(string id)
    {
        id = (id ?? "").Replace(" ", "");
        return id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
    }

    private static string HumanSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = bytes; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{s:0.#} {u[i]}";
    }
}
