using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Service;

/// <summary>
/// Launches a process into the active console session on a given desktop, running
/// as LocalSystem (the service's own account). Running as SYSTEM on the active
/// INPUT desktop is what lets the worker capture and inject on both the normal
/// desktop AND the secure desktop (UAC prompt / lock screen / Ctrl+Alt+Del) — a
/// normal user process cannot touch the secure desktop.
///
/// Technique: duplicate the service's SYSTEM token, retarget it at the active
/// session, and CreateProcessAsUser with lpDesktop = "WinSta0\\{desktop}".
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionLauncher
{
    /// <summary>The active console session, or 0xFFFFFFFF if none (e.g. no one logged on).</summary>
    public static uint ActiveSessionId => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Name of the current input desktop of the interactive window station WinSta0
    /// (e.g. "Default", or "Winlogon" while a UAC prompt / the lock screen shows).
    /// The service lives on its own window station, so we attach to WinSta0 to read
    /// it, then restore. Returns "Default" on failure (a safe launch target).
    /// </summary>
    public static string CurrentInputDesktop()
    {
        IntPtr prevWinSta = GetProcessWindowStation();
        IntPtr winSta0 = OpenWindowStation("WinSta0", false, WINSTA_ENUMDESKTOPS | READ_CONTROL);
        if (winSta0 == IntPtr.Zero) return "Default";
        try
        {
            if (!SetProcessWindowStation(winSta0)) return "Default";
            IntPtr hDesk = OpenInputDesktop(0, false, READ_CONTROL | DESKTOP_READOBJECTS);
            if (hDesk == IntPtr.Zero) return "Default";
            try
            {
                var sb = new byte[256];
                if (GetUserObjectInformation(hDesk, UOI_NAME, sb, sb.Length, out int needed))
                {
                    var name = System.Text.Encoding.Unicode.GetString(sb, 0, Math.Max(0, needed - 2)).Trim('\0');
                    return string.IsNullOrWhiteSpace(name) ? "Default" : name;
                }
                return "Default";
            }
            finally { CloseDesktop(hDesk); }
        }
        finally
        {
            if (prevWinSta != IntPtr.Zero) SetProcessWindowStation(prevWinSta);
            CloseWindowStation(winSta0);
        }
    }

    /// <summary>
    /// Start <paramref name="exePath"/> (with args) in the active session on
    /// desktop "WinSta0\\{desktopName}", as SYSTEM. Returns the process id, or 0.
    /// </summary>
    public static uint LaunchInSession(uint sessionId, string exePath, string args, string desktopName)
    {
        // Start from the service's own process token and duplicate a primary token
        // we can retarget at the session.
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, out IntPtr hToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");

        IntPtr dupToken = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");

            // Retarget the duplicated token at the active session.
            GCHandle h = GCHandle.Alloc(sessionId, GCHandleType.Pinned);
            try
            {
                if (!SetTokenInformation(dupToken, TokenSessionId, h.AddrOfPinnedObject(), sizeof(uint)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(SessionId)");
            }
            finally { h.Free(); }

            CreateEnvironmentBlock(out env, dupToken, false);

            string desktop = $"WinSta0\\{desktopName}";
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

            string cmd = $"\"{exePath}\" {args}";
            if (!CreateProcessAsUser(dupToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    flags, env, Path.GetDirectoryName(exePath), ref si, out PROCESS_INFORMATION pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            CloseHandle(hToken);
        }
    }

    // --------------------------- P/Invoke ---------------------------
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ASSIGN_PRIMARY = 0x0001,
        TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000;
    private const uint READ_CONTROL = 0x00020000, DESKTOP_READOBJECTS = 0x0001;
    private const uint WINSTA_ENUMDESKTOPS = 0x0001;
    private const int UOI_NAME = 2;

    // SECURITY_IMPERSONATION_LEVEL / TOKEN_TYPE / TOKEN_INFORMATION_CLASS
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, ref SECURITY_ATTRIBUTES sa, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int cls, IntPtr info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmd, IntPtr procAttr,
        IntPtr threadAttr, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr hDesk);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int index, byte[] info, int len, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr hWinSta);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseWindowStation(IntPtr hWinSta);
}
