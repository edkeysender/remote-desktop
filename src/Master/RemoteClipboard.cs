using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Threading;

namespace RemoteDesktop.Master;

/// <summary>
/// Lazy remote-clipboard files. When files are copied on the remote, this puts a
/// *virtual* file list on the local clipboard (CFSTR_FILEDESCRIPTORW + CFSTR_FILECONTENTS)
/// — nothing is transferred at Ctrl+C time. When the user actually pastes, Explorer asks
/// for each file's contents and only then is it downloaded over the session's file channel.
/// Runs on its own STA pump thread: OLE delivers the paste-time callbacks to the thread
/// that owns the clipboard, so downloads never block the app's UI thread.
/// </summary>
internal sealed class RemoteClipboard : System.Runtime.InteropServices.ComTypes.IDataObject
{
    // ---- static: single pump thread that owns the clipboard object ----
    private static readonly object Gate = new();
    private static Dispatcher? _pump;
    private static RemoteClipboard? _current;

    private static Dispatcher Pump()
    {
        lock (Gate)
        {
            if (_pump != null) return _pump;
            using var ready = new ManualResetEventSlim();
            Dispatcher? d = null;
            var t = new Thread(() =>
            {
                OleInitialize(IntPtr.Zero);
                d = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "RemoteClipboard" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
            _pump = d!;
            return _pump;
        }
    }

    /// <summary>Advertise the remote files on the local clipboard (no data transferred yet).</summary>
    public static void Set(List<RemoteClipFile> files, ViewerSession session)
        => Pump().Invoke(() =>
        {
            _current = new RemoteClipboard(files, session);
            OleSetClipboard(_current);
        });

    /// <summary>Drop our clipboard entry when its session goes away (a paste could never succeed).</summary>
    public static void ClearIfOwned(ViewerSession session)
    {
        Dispatcher? p; lock (Gate) p = _pump;
        p?.BeginInvoke(() =>
        {
            if (_current?._session == session)
            {
                try { OleSetClipboard(null); } catch { }
                _current = null;
            }
        });
    }

    // ---- instance ----
    private readonly List<RemoteClipFile> _files;
    private readonly ViewerSession _session;
    private readonly string?[] _localPaths;    // per-file staged path once downloaded

    private RemoteClipboard(List<RemoteClipFile> files, ViewerSession session)
    {
        _files = files; _session = session;
        _localPaths = new string?[files.Count];
    }

    private string NameOf(int i) =>
        string.IsNullOrEmpty(_files[i].Name) ? Path.GetFileName(_files[i].Path) : _files[i].Name;

    private static readonly short CfDescriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short CfContents = (short)RegisterClipboardFormat("FileContents");
    private static readonly short CfDropEffect = (short)RegisterClipboardFormat("Preferred DropEffect");

    // ---- IDataObject ----
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == CfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildDescriptor();
            return;
        }
        if (format.cfFormat == CfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0
            && format.lindex >= 0 && format.lindex < _files.Count)
        {
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = StreamFor(format.lindex);
            return;
        }
        if (format.cfFormat == CfDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            var h = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(h, 1);   // DROPEFFECT_COPY
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = h;
            return;
        }
        throw new COMException("unsupported format", DV_E_FORMATETC);
    }

    private IntPtr BuildDescriptor()
    {
        int descSize = Marshal.SizeOf<FILEDESCRIPTORW>();
        var h = Marshal.AllocHGlobal(4 + descSize * _files.Count);
        Marshal.WriteInt32(h, _files.Count);
        for (int i = 0; i < _files.Count; i++)
        {
            var d = new FILEDESCRIPTORW
            {
                dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI,
                dwFileAttributes = 0x80,   // FILE_ATTRIBUTE_NORMAL
                nFileSizeLow = (uint)(_files[i].Size & 0xFFFFFFFF),
                nFileSizeHigh = (uint)(_files[i].Size >> 32),
                cFileName = NameOf(i),
            };
            Marshal.StructureToPtr(d, h + 4 + i * descSize, false);
        }
        return h;
    }

    /// <summary>Called by Explorer at paste time — download the file now, hand back a stream.</summary>
    private IntPtr StreamFor(int i)
    {
        var local = _localPaths[i];
        if (local == null || !File.Exists(local))
        {
            var stage = Path.Combine(Path.GetTempPath(), "Remotler", "clip", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            // Task.Run so the download's continuations run on the thread pool, not on this
            // (blocked) STA dispatcher — awaiting inline here would deadlock.
            var remotePath = _files[i].Path;
            local = Task.Run(() => _session.Files.DownloadAsync(remotePath, Path.Combine(stage, NameOf(i))))
                        .GetAwaiter().GetResult();
            _localPaths[i] = local;
        }
        int hr = SHCreateStreamOnFileEx(local, STGM_READ | STGM_SHARE_DENY_NONE, 0x80, false, IntPtr.Zero, out IntPtr stream);
        if (hr != 0) throw new COMException("stream open failed", hr);
        return stream;
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        if (format.cfFormat == CfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0) return 0;
        if (format.cfFormat == CfDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        return DV_E_FORMATETC;
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("set not supported", E_NOTIMPL);
        var fmts = new[]
        {
            new FORMATETC { cfFormat = CfDescriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = CfContents,  dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = 0,  tymed = TYMED.TYMED_ISTREAM },
            new FORMATETC { cfFormat = CfDropEffect, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        };
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)fmts.Length, fmts, out var e));
        return e;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("not supported", E_NOTIMPL);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return DATA_S_SAMEFORMATETC; }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new COMException("not supported", E_NOTIMPL);
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return OLE_E_ADVISENOTSUPPORTED; }

    // ---- interop ----
    private const uint FD_ATTRIBUTES = 0x04, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000;
    private const uint STGM_READ = 0x0, STGM_SHARE_DENY_NONE = 0x40;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizelcx, sizelcy, pointlx, pointly;
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, FORMATETC[] formats, out IEnumFORMATETC ppenum);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint grfMode, uint attributes, bool create, IntPtr reserved, out IntPtr stream);
}
