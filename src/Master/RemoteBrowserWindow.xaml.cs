using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// Browses the remote (client) machine's filesystem over the file data channel and
/// transfers both ways: download a selected remote file, or upload a local file into
/// the folder currently shown. All operations are driven from here; the client has no
/// transfer UI of its own — it just answers listing/pull requests and receives uploads.
/// </summary>
public partial class RemoteBrowserWindow : Window
{
    private sealed record Row(string Name, string SizeText, bool IsDir, string FullPath);

    private readonly ViewerSession _session;
    private string _current = "";        // "" = the drive list ("This PC")
    private bool _busy;
    private bool _gotFirstListing;
    private DispatcherTimer? _openTimer;

    public RemoteBrowserWindow(ViewerSession session)
    {
        InitializeComponent();
        _session = session;

        _session.Files.ListingReceived += OnListing;
        _session.Files.ListingError += OnListingError;
        _session.Files.ReceiveProgress += OnReceiveProgress;
        _session.Files.SendProgress += OnSendProgress;

        Loaded += (_, _) => StartInitialLoad();
        Closed += (_, _) =>
        {
            _openTimer?.Stop();
            _session.Files.ListingReceived -= OnListing;
            _session.Files.ListingError -= OnListingError;
            _session.Files.ReceiveProgress -= OnReceiveProgress;
            _session.Files.SendProgress -= OnSendProgress;
        };
    }

    // The file channel may open a moment after the session connects; poll until it's
    // ready, then request the drive list. Give up with a message after ~15s.
    private void StartInitialLoad()
    {
        int ticks = 0;
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _openTimer.Tick += (_, _) =>
        {
            if (_gotFirstListing) { _openTimer!.Stop(); return; }
            if (_session.Files.IsOpen) _session.Files.RequestListing(_current);
            else if (++ticks > 30) { _openTimer!.Stop(); StatusText.Text = "File channel didn't open — try reconnecting."; }
        };
        _openTimer.Start();
        if (_session.Files.IsOpen) _session.Files.RequestListing(_current);
    }

    private void Navigate(string path)
    {
        if (_busy) return;
        StatusText.Text = "Loading…";
        _session.Files.RequestListing(path);
    }

    private void OnListing(FsListing listing) => Dispatcher.Invoke(() =>
    {
        _gotFirstListing = true;
        _current = listing.Path;
        PathText.Text = _current.Length == 0 ? "This PC" : _current;
        UpBtn.IsEnabled = _current.Length > 0;
        UploadBtn.IsEnabled = _current.Length > 0;   // can't upload into the drive list

        var rows = listing.Entries
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new Row(
                (e.IsDir ? "📁 " : "📄 ") + e.Name,
                e.IsDir ? "" : HumanSize(e.Size),
                e.IsDir,
                _current.Length == 0 ? e.Name : Path.Combine(_current, e.Name)))
            .ToList();
        List.ItemsSource = rows;
        StatusText.Text = $"{rows.Count(r => r.IsDir)} folder(s), {rows.Count(r => !r.IsDir)} file(s)";
    });

    private void OnListingError(string path, string msg) =>
        Dispatcher.Invoke(() => StatusText.Text = $"Can't open {(path.Length == 0 ? "This PC" : path)}: {msg}");

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not Row row) return;
        if (row.IsDir) Navigate(row.FullPath);
        else _ = DownloadAsync(row);
    }

    private void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var parent = Path.GetDirectoryName(_current.TrimEnd('\\', '/'));
        Navigate(parent ?? "");   // a drive root's parent is the drive list ("")
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Navigate(_current);

    private void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is Row { IsDir: false } row) _ = DownloadAsync(row);
        else StatusText.Text = "Select a file to download.";
    }

    private async Task DownloadAsync(Row row)
    {
        if (_busy) return;
        var name = row.Name.Length > 2 ? row.Name[2..] : row.Name;   // strip the "📄 " prefix
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Title = "Save remote file as" };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        try
        {
            var saved = await _session.Files.DownloadAsync(row.FullPath, dlg.FileName);
            StatusText.Text = $"Downloaded → {saved}";
        }
        catch (Exception ex) { StatusText.Text = "Download failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void UploadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _current.Length == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Upload a file to the remote folder" };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        try
        {
            await _session.Files.SendFileAsync(dlg.FileName, destDir: _current);
            StatusText.Text = $"Uploaded {Path.GetFileName(dlg.FileName)} → {_current}";
            Navigate(_current);   // refresh so the new file shows
        }
        catch (Exception ex) { StatusText.Text = "Upload failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private int _lastPct = -1;
    private void OnReceiveProgress(string n, long done, long total) => Progress("Downloading", n, done, total);
    private void OnSendProgress(string n, long done, long total) => Progress("Uploading", n, done, total);
    private void Progress(string verb, string name, long done, long total)
    {
        int pct = total > 0 ? (int)(done * 100 / total) : 100;
        if (pct == _lastPct) return;
        _lastPct = pct;
        Dispatcher.BeginInvoke(() => StatusText.Text = $"{verb} {name}… {pct}%");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _lastPct = -1;
        UpBtn.IsEnabled = !busy && _current.Length > 0;
        RefreshBtn.IsEnabled = !busy;
        DownloadBtn.IsEnabled = !busy;
        UploadBtn.IsEnabled = !busy && _current.Length > 0;
        List.IsEnabled = !busy;
    }

    private static string HumanSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = bytes; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{s:0.#} {u[i]}";
    }
}
