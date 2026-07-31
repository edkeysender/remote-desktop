using System.IO;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace RemoteDesktop.Shared;

/// <summary>
/// Bidirectional file transfer over a dedicated WebRTC data channel ("file").
/// The channel is reliable + ordered (SCTP default), so the protocol is simple:
///
///   sender   → {"t":"begin","name","size"}   announce one file
///   sender   → binary chunks (16 KB)          file bytes, in order
///   sender   → {"t":"end"}                    all bytes sent
///   receiver → {"t":"ack","n":bytes}          progress ack, drives sender's window
///   receiver → {"t":"done"} / {"t":"err"}     saved ok / failed
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
    private long _txAcked;
    private TaskCompletionSource<bool>? _txDone;     // completed by receiver's "done"/"err"
    private string? _txError;

    // --- receive state ---
    private FileStream? _rxStream;
    private string? _rxPath;
    private string? _rxName;
    private long _rxSize, _rxReceived, _rxLastAck;

    /// <summary>Where received files are written. Defaults to the user's Downloads folder.</summary>
    public string SaveDirectory { get; set; } = DefaultSaveDirectory();

    public event Action<string, long>? ReceiveStarted;              // name, total bytes
    public event Action<string, long, long>? ReceiveProgress;       // name, received, total
    public event Action<string, string>? ReceiveCompleted;          // name, saved path
    public event Action<string, long, long>? SendProgress;          // name, sent, total
    public event Action<string>? Error;

    public bool IsOpen => _chan is { IsOpened: true };

    /// <summary>Bind to the session's "file" data channel (called on (re)connect).</summary>
    public void Attach(RTCDataChannel chan)
    {
        _chan = chan;
        chan.onmessage += OnMessage;
        chan.onclose += AbortReceive;
    }

    /// <summary>Session ended: drop the channel, discard any partial download.</summary>
    public void Detach()
    {
        _chan = null;
        AbortReceive();
        _txDone?.TrySetResult(false);
    }

    // ------------------------------- sending -------------------------------

    public async Task SendFileAsync(string path, CancellationToken ct = default)
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
            _txDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            chan.send(JsonSerializer.Serialize(new { t = "begin", name, size }));

            using var fs = File.OpenRead(path);
            var buf = new byte[ChunkSize];
            int n;
            while ((n = await fs.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                // Flow control: stall while more than Window bytes are unacked.
                while (sent + n - Interlocked.Read(ref _txAcked) > Window)
                {
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
                Directory.CreateDirectory(SaveDirectory);
                _rxPath = UniquePath(SaveDirectory, name);
                _rxName = name;
                _rxSize = r.GetProperty("size").GetInt64();
                _rxReceived = 0; _rxLastAck = 0;
                _rxStream = new FileStream(_rxPath, FileMode.CreateNew, FileAccess.Write);
                ReceiveStarted?.Invoke(name, _rxSize);
                break;
            }
            case "end":
            {
                if (_rxStream == null) break;
                _rxStream.Dispose(); _rxStream = null;
                _chan?.send(JsonSerializer.Serialize(new { t = "ack", n = _rxReceived }));
                _chan?.send("""{"t":"done"}""");
                ReceiveCompleted?.Invoke(_rxName!, _rxPath!);
                _rxPath = null; _rxName = null;
                break;
            }
            case "ack":
                Interlocked.Exchange(ref _txAcked, r.GetProperty("n").GetInt64());
                break;
            case "done":
                _txDone?.TrySetResult(true);
                break;
            case "err":
                _txError = r.TryGetProperty("msg", out var m) ? m.GetString() : "remote error";
                _txDone?.TrySetResult(false);
                break;
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
