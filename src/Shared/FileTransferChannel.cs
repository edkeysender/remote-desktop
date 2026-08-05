using System.IO;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace RemoteDesktop.Shared;

/// <summary>One entry in a remote directory listing.</summary>
public sealed record FsEntry(string Name, bool IsDir, long Size);

/// <summary>A remote directory listing. <see cref="Path"/> is "" for the drive list ("This PC").</summary>
public sealed record FsListing(string Path, IReadOnlyList<FsEntry> Entries);

/// <summary>
/// Bidirectional file transfer + remote file browsing over a dedicated WebRTC data
/// channel ("file"). The channel is reliable + ordered (SCTP default), so the protocol
/// is simple:
///
///   sender   → {"t":"begin","name","size","dest"?}  announce one file (dest = target dir)
///   sender   → binary chunks (16 KB)                 file bytes, in order
///   sender   → {"t":"end"}                           all bytes sent
///   receiver → {"t":"ack","n":bytes}                 progress ack, drives sender's window
///   receiver → {"t":"done"} / {"t":"err"}            saved ok / failed
///
/// Remote browsing (master drives it; the client answers from its own filesystem):
///   master   → {"t":"ls","path"}          list a directory ("" = drives)
///   client   → {"t":"ls-ok","path","entries":[{n,d,s}]} / {"t":"ls-err","path","msg"}
///   master   → {"t":"get","path"}         pull a remote file (client replies with begin/…)
///   client   → {"t":"get-err","msg"}      the pull could not start
///
/// One transfer at a time on the channel (either direction) — binary chunks carry
/// no id, so interleaving would be ambiguous. App-level acks provide flow control
/// (window) so a big file can't balloon the SCTP send buffer.
/// Both sides of the app (host and master) use this same class.
/// </summary>
public sealed class FileTransferChannel
{
    private const int ChunkSize = 16 * 1024;
    private const long Window = 1024 * 1024;        // max unacked bytes in flight
    private const long AckEvery = 256 * 1024;       // receiver acks at this granularity

    private RTCDataChannel? _chan;

    // --- send state ---
    private int _sending;                            // 0/1 gate: one outbound transfer at a time
    private volatile bool _txCancel;                 // abort the current outbound transfer
    private long _txAcked;
    private TaskCompletionSource<bool>? _txDone;     // completed by receiver's "done"/"err"
    private string? _txError;

    // --- receive state ---
    private FileStream? _rxStream;
    private string? _rxPath;
    private string? _rxName;
    private string? _rxOrigName;                      // name the sender announced (key for "reveal")
    private long _rxSize, _rxReceived, _rxLastAck;

    // Host-side: original announced name -> absolute path where we saved it, so a later
    // "reveal" request from the viewer can locate the pushed file in Explorer.
    private readonly Dictionary<string, string> _rxSaved = new(StringComparer.OrdinalIgnoreCase);

    // --- pull state (master waiting on a file it requested with "get") ---
    private TaskCompletionSource<string>? _pullTcs;
    private string? _pullSaveAs;                      // explicit save path for the pulled file

    /// <summary>Where received files are written. Defaults to the user's Downloads folder.</summary>
    public string SaveDirectory { get; set; } = DefaultSaveDirectory();

    /// <summary>Host-side: resolves the special "@current" push target to a real directory
    /// (e.g. the folder the user has open in Explorer). Null / missing → falls back to Downloads.</summary>
    public Func<string?>? ResolveDropDir { get; set; }

    /// <summary>Sentinel dest that asks the receiver to save into its current Explorer folder.</summary>
    public const string CurrentFolderToken = "@current";

    public event Action<string, long>? ReceiveStarted;              // name, total bytes
    public event Action<string, long, long>? ReceiveProgress;       // name, received, total
    public event Action<string, string>? ReceiveCompleted;          // name, saved path
    public event Action<string, long, long>? SendProgress;          // name, sent, total
    public event Action<string>? Error;
    public event Action<FsListing>? ListingReceived;                // master: a remote directory arrived
    public event Action<string, string>? ListingError;             // master: path, error message
    public event Action<string>? RevealRequested;                   // host: reveal this absolute path in Explorer

    public bool IsOpen => _chan is { IsOpened: true };

    /// <summary>Bind to the session's "file" data channel (called on (re)connect).</summary>
    public void Attach(RTCDataChannel chan)
    {
        _chan = chan;
        chan.onmessage += OnMessage;
        chan.onclose += AbortReceive;
    }

    /// <summary>Session ended: drop the channel, abort + discard any in-flight transfer.</summary>
    public void Detach()
    {
        _txCancel = true;                 // stop any outbound send loop
        _chan = null;
        AbortReceive();                   // discard any partial download
        _txDone?.TrySetResult(false);
        _pullTcs?.TrySetException(new IOException("Session ended."));
    }

    /// <summary>Fires when a transfer is aborted (locally or by the peer).</summary>
    public event Action<string>? Cancelled;

    /// <summary>Abort the current transfer (either direction) and tell the peer to stop too.</summary>
    public void Cancel()
    {
        _txCancel = true;
        try { _chan?.send("""{"t":"cancel"}"""); } catch { }
        AbortReceive();
        _txDone?.TrySetResult(false);
        _pullTcs?.TrySetException(new OperationCanceledException("Transfer cancelled."));
        Cancelled?.Invoke("Transfer cancelled");
    }

    // ------------------------------- sending -------------------------------

    /// <summary>
    /// Send a local file to the peer. <paramref name="destDir"/>, if given, asks the
    /// receiver to save into that (already-existing) directory instead of its Downloads.
    /// </summary>
    public async Task SendFileAsync(string path, string? destDir = null, CancellationToken ct = default)
    {
        var chan = _chan;
        if (chan is not { IsOpened: true }) throw new InvalidOperationException("File channel is not open.");
        if (Interlocked.Exchange(ref _sending, 1) == 1) throw new InvalidOperationException("A file is already being sent.");
        try
        {
            var info = new FileInfo(path);
            string name = info.Name;
            long size = info.Length, sent = 0;
            Interlocked.Exchange(ref _txAcked, 0);
            _txError = null;
            _txCancel = false;
            _txDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            chan.send(JsonSerializer.Serialize(new { t = "begin", name, size, dest = destDir }));

            using var fs = File.OpenRead(path);
            var buf = new byte[ChunkSize];
            int n;
            while ((n = await fs.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                if (_txCancel) throw new OperationCanceledException("Transfer cancelled.");
                // Flow control: stall while more than Window bytes are unacked.
                while (sent + n - Interlocked.Read(ref _txAcked) > Window)
                {
                    if (_txCancel) throw new OperationCanceledException("Transfer cancelled.");
                    if (_chan is not { IsOpened: true }) throw new IOException("File channel closed mid-transfer.");
                    if (_txError != null) throw new IOException(_txError);
                    await Task.Delay(10, ct);
                }
                var chunk = new byte[n];
                Array.Copy(buf, chunk, n);
                chan.send(chunk);
                sent += n;
                SendProgress?.Invoke(name, sent, size);
            }
            chan.send("""{"t":"end"}""");

            // Wait for the receiver to confirm it wrote everything (or report failure).
            var finished = await Task.WhenAny(_txDone.Task, Task.Delay(60_000, ct));
            if (_txError != null) throw new IOException(_txError);
            if (finished != _txDone.Task || !_txDone.Task.Result)
                throw new IOException("Transfer not confirmed by the remote side.");
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    // ---------------------- remote browsing / pull (master) ----------------------

    /// <summary>Ask the peer for a directory listing ("" = the drive list). Master side.</summary>
    public void RequestListing(string path)
    {
        _chan?.send(JsonSerializer.Serialize(new { t = "ls", path }));
    }

    /// <summary>Master side: ask the host to reveal a file we pushed (by its announced name)
    /// in Explorer on the remote machine.</summary>
    public void RequestReveal(string name)
    {
        _chan?.send(JsonSerializer.Serialize(new { t = "reveal", name }));
    }

    /// <summary>
    /// Pull a remote file to this machine and return the local path it was saved to.
    /// <paramref name="saveAsPath"/> forces an exact destination; otherwise it lands in
    /// <see cref="SaveDirectory"/>. Master side.
    /// </summary>
    public async Task<string> DownloadAsync(string remotePath, string? saveAsPath = null, CancellationToken ct = default)
    {
        var chan = _chan;
        if (chan is not { IsOpened: true }) throw new InvalidOperationException("File channel is not open.");
        if (_pullTcs != null || _rxStream != null) throw new InvalidOperationException("A transfer is already in progress.");

        _pullSaveAs = saveAsPath;
        _pullTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            chan.send(JsonSerializer.Serialize(new { t = "get", path = remotePath }));
            using (ct.Register(() => _pullTcs?.TrySetCanceled()))
            {
                var done = await Task.WhenAny(_pullTcs.Task, Task.Delay(TimeSpan.FromMinutes(30), ct));
                if (done != _pullTcs.Task) throw new IOException("Download timed out.");
                return await _pullTcs.Task;
            }
        }
        finally
        {
            _pullTcs = null;
            _pullSaveAs = null;
        }
    }

    // ------------------------------ receiving ------------------------------

    private void OnMessage(RTCDataChannel dc, DataChannelPayloadProtocols proto, byte[] data)
    {
        try
        {
            if (proto == DataChannelPayloadProtocols.WebRTC_String)
                OnControl(Encoding.UTF8.GetString(data));
            else
                OnChunk(data);
        }
        catch (Exception ex)
        {
            try { _chan?.send(JsonSerializer.Serialize(new { t = "err", msg = ex.Message })); } catch { }
            AbortReceive();
            Error?.Invoke(ex.Message);
        }
    }

    private void OnControl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        switch (r.GetProperty("t").GetString())
        {
            case "begin":
            {
                if (_rxStream != null) throw new IOException("A transfer is already in progress.");
                // Never trust the remote name as a path — strip to a bare file name.
                var name = Path.GetFileName(r.GetProperty("name").GetString() ?? "file");
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                if (name.Length == 0) name = "file";
                _rxOrigName = name;   // remember for a later "reveal" request from the viewer

                if (_pullSaveAs != null)
                {
                    // A pull we initiated with an explicit destination path.
                    Directory.CreateDirectory(Path.GetDirectoryName(_pullSaveAs)!);
                    _rxPath = _pullSaveAs;
                }
                else
                {
                    // A push: honor a requested target dir. "@current" resolves to the folder
                    // the user has open in Explorer (via ResolveDropDir); else Downloads.
                    var dest = r.TryGetProperty("dest", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
                    if (dest == CurrentFolderToken) dest = ResolveDropDir?.Invoke();
                    var dir = !string.IsNullOrEmpty(dest) && Directory.Exists(dest) ? dest! : SaveDirectory;
                    Directory.CreateDirectory(dir);
                    _rxPath = UniquePath(dir, name);
                }
                _rxName = Path.GetFileName(_rxPath);
                _rxSize = r.GetProperty("size").GetInt64();
                _rxReceived = 0; _rxLastAck = 0;
                _rxStream = new FileStream(_rxPath, FileMode.CreateNew, FileAccess.Write);
                ReceiveStarted?.Invoke(_rxName, _rxSize);
                break;
            }
            case "end":
            {
                if (_rxStream == null) break;
                _rxStream.Dispose(); _rxStream = null;
                var savedPath = _rxPath!; var savedName = _rxName!;
                if (_rxOrigName != null) _rxSaved[_rxOrigName] = savedPath;   // for "reveal"
                _chan?.send(JsonSerializer.Serialize(new { t = "ack", n = _rxReceived }));
                _chan?.send("""{"t":"done"}""");
                _rxPath = null; _rxName = null; _rxOrigName = null;
                ReceiveCompleted?.Invoke(savedName, savedPath);
                _pullTcs?.TrySetResult(savedPath);   // unblock DownloadAsync, if this was a pull
                break;
            }
            case "ack":
                Interlocked.Exchange(ref _txAcked, r.GetProperty("n").GetInt64());
                break;
            case "cancel":               // peer aborted — stop sending and/or discard the partial
                _txCancel = true;
                AbortReceive();
                _txDone?.TrySetResult(false);
                Cancelled?.Invoke("Transfer cancelled by the other side");
                break;
            case "done":
                _txDone?.TrySetResult(true);
                break;
            case "err":
                _txError = r.TryGetProperty("msg", out var m) ? m.GetString() : "remote error";
                _txDone?.TrySetResult(false);
                break;

            // ---- remote browsing: the client answers, the master consumes ----
            case "ls":
                SendListing(r.GetProperty("path").GetString() ?? "");
                break;
            case "ls-ok":
            {
                var p = r.GetProperty("path").GetString() ?? "";
                var entries = new List<FsEntry>();
                foreach (var e in r.GetProperty("entries").EnumerateArray())
                    entries.Add(new FsEntry(
                        e.GetProperty("n").GetString() ?? "",
                        e.GetProperty("d").GetBoolean(),
                        e.TryGetProperty("s", out var sz) ? sz.GetInt64() : 0));
                ListingReceived?.Invoke(new FsListing(p, entries));
                break;
            }
            case "ls-err":
                ListingError?.Invoke(
                    r.GetProperty("path").GetString() ?? "",
                    r.TryGetProperty("msg", out var lm) ? lm.GetString() ?? "" : "");
                break;
            case "get":
                _ = HandleGetAsync(r.GetProperty("path").GetString() ?? "");
                break;
            case "get-err":
                _pullTcs?.TrySetException(new IOException(
                    r.TryGetProperty("msg", out var gm) ? gm.GetString() : "remote error"));
                break;

            // ---- host side: viewer asks to reveal a pushed file in Explorer here ----
            case "reveal":
            {
                var nm = r.TryGetProperty("name", out var rv) ? rv.GetString() : null;
                string? target = null;
                if (!string.IsNullOrEmpty(nm))
                {
                    if (string.Equals(_rxOrigName, nm, StringComparison.OrdinalIgnoreCase) && _rxPath != null)
                        target = _rxPath;                              // still receiving — reveal in place
                    else if (_rxSaved.TryGetValue(nm!, out var sp))
                        target = sp;                                   // finished earlier
                }
                if (string.IsNullOrEmpty(target)) target = ResolveDropDir?.Invoke();   // fallback: current folder
                if (!string.IsNullOrEmpty(target)) RevealRequested?.Invoke(target!);
                break;
            }
        }
    }

    // ---- client side: answer a listing request from its own filesystem ----
    private void SendListing(string path)
    {
        try
        {
            object payload;
            if (string.IsNullOrEmpty(path))
            {
                var drives = DriveInfo.GetDrives()
                    .Where(dr => dr.IsReady)
                    .Select(dr => new { n = dr.Name, d = true, s = 0L });
                payload = new { t = "ls-ok", path = "", entries = drives };
            }
            else
            {
                var dirs = Directory.GetDirectories(path)
                    .Select(p => new { n = Path.GetFileName(p), d = true, s = 0L });
                var files = Directory.GetFiles(path).Select(p =>
                {
                    long len = 0; try { len = new FileInfo(p).Length; } catch { }
                    return new { n = Path.GetFileName(p), d = false, s = len };
                });
                payload = new { t = "ls-ok", path, entries = dirs.Concat(files) };
            }
            _chan?.send(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            try { _chan?.send(JsonSerializer.Serialize(new { t = "ls-err", path, msg = ex.Message })); } catch { }
        }
    }

    // ---- client side: serve a pull request by sending the file back ----
    private async Task HandleGetAsync(string path)
    {
        try { await SendFileAsync(path); }
        catch (Exception ex)
        {
            try { _chan?.send(JsonSerializer.Serialize(new { t = "get-err", msg = ex.Message })); } catch { }
        }
    }

    private void OnChunk(byte[] data)
    {
        if (_rxStream == null) return;                  // stray chunk after abort — drop
        _rxStream.Write(data, 0, data.Length);
        _rxReceived += data.Length;
        if (_rxReceived - _rxLastAck >= AckEvery)
        {
            _rxLastAck = _rxReceived;
            _chan?.send(JsonSerializer.Serialize(new { t = "ack", n = _rxReceived }));
        }
        ReceiveProgress?.Invoke(_rxName!, _rxReceived, _rxSize);
    }

    private void AbortReceive()
    {
        _pullTcs?.TrySetException(new IOException("Transfer aborted."));
        var s = _rxStream; _rxStream = null;
        if (s == null) return;
        try { s.Dispose(); } catch { }
        try { if (_rxPath != null) File.Delete(_rxPath); } catch { }   // discard partial file
        _rxPath = null; _rxName = null;
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>
    /// The user's Downloads folder — except under the unattended worker (runs as
    /// SYSTEM, whose profile is system32\config\systemprofile), where files land in
    /// the world-readable Public Downloads instead.
    /// </summary>
    private static string DefaultSaveDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!profile.Contains(@"\config\systemprofile", StringComparison.OrdinalIgnoreCase))
        {
            var dl = Path.Combine(profile, "Downloads");
            try { Directory.CreateDirectory(dl); return dl; } catch { }
        }
        var pub = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        return Path.Combine(pub, "Downloads");
    }
}
