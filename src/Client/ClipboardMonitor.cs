using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Polls the host's clipboard so that a copy made on the remote machine (text or files)
/// can be pushed to the viewer — the remote→local half of clipboard sync. Poll-based so it
/// needs no window/message loop. Loop-safe via a "last value" guard that the host updates
/// whenever it sets the clipboard from the viewer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClipboardMonitor : IDisposable
{
    private readonly Action<string> _onText;
    private readonly Action<string[]> _onFiles;
    private System.Threading.Timer? _timer;
    private string _lastText = "";
    private string _lastFilesSig = "";
    private int _busy;

    public ClipboardMonitor(Action<string> onText, Action<string[]> onFiles)
    { _onText = onText; _onFiles = onFiles; }

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 800, 800);

    /// <summary>Record text we just set from the viewer, so we don't echo it straight back.</summary>
    public void NoteText(string text) { _lastText = text ?? ""; _lastFilesSig = ""; }

    private void Tick()
    {
        if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var files = ClipboardHelper.GetFiles();
            if (files.Length > 0)
            {
                var sig = string.Join("|", files);
                if (sig != _lastFilesSig) { _lastFilesSig = sig; _lastText = ""; _onFiles(files); }
                return;
            }
            var t = ClipboardHelper.GetText();
            if (!string.IsNullOrEmpty(t) && t != _lastText) { _lastText = t!; _lastFilesSig = ""; _onText(t!); }
        }
        catch { /* clipboard briefly locked; try next tick */ }
        finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose() { _timer?.Dispose(); _timer = null; }
}
