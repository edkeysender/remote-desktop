using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>One file in an update, with the hash the download must match.</summary>
public sealed record UpdateFile(string Name, string Sha256, long Size);

/// <summary>An available update newer than what's running.</summary>
public sealed record UpdateInfo(Version Version, string Notes, IReadOnlyList<UpdateFile> Files);

/// <summary>
/// Self-update against the project's GitHub Releases. <c>releases/latest/download/manifest.json</c>
/// always resolves to the newest published release's manifest, which lists the version and,
/// per component ("app" / "client"), the files to fetch (each a flat release asset at
/// <c>releases/latest/download/&lt;name&gt;</c>). If the manifest version is newer than the
/// running assembly, the app shows an "update available" button; clicking downloads the files
/// (hash-verified) and hands off to a small PowerShell script that waits for the app to exit,
/// swaps the files in place, and relaunches. A new git tag = an update in the field.
///
/// The client component elevates (the exe lives in Program Files and a SYSTEM service may
/// hold it open); the app installs per-user and updates without a UAC prompt.
/// </summary>
public sealed class Updater
{
    // Auto-update source: GitHub Releases. `latest/download/<asset>` is a stable URL that
    // redirects to the newest release's asset (HttpClient follows redirects by default).
    private const string UpdateBase = "https://github.com/edkeysender/remote-desktop/releases/latest/download/";

    private readonly string _component;      // "app" | "client"
    private readonly bool _elevate;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public Updater(string component, bool elevate)
    {
        _component = component;
        _elevate = elevate;
    }

    /// <summary>The running app's version, normalized to Major.Minor.Build.</summary>
    public static Version Current { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Fetch the manifest and return an <see cref="UpdateInfo"/> if a newer version is
    /// offered for this component; otherwise null. Never throws — a missing manifest,
    /// unreachable server, or malformed JSON just means "no update".
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string serverUrl, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(UpdateBase + "manifest.json", ct));
            var root = doc.RootElement;
            var version = Normalize(Version.Parse(root.GetProperty("version").GetString()!));
            if (version <= Current) return null;
            if (!root.TryGetProperty(_component, out var comp)) return null;

            // Tolerate either an array of files or a single file object (PowerShell's
            // ConvertTo-Json collapses a one-element array to a bare object).
            var files = new List<UpdateFile>();
            if (comp.ValueKind == JsonValueKind.Array)
                foreach (var f in comp.EnumerateArray()) files.Add(ReadFile(f));
            else if (comp.ValueKind == JsonValueKind.Object)
                files.Add(ReadFile(comp));
            else return null;
            if (files.Count == 0) return null;

            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            return new UpdateInfo(version, notes, files);
        }
        catch { return null; }
    }

    private static UpdateFile ReadFile(JsonElement f) => new(
        f.GetProperty("name").GetString()!,
        f.GetProperty("sha256").GetString()!,
        f.TryGetProperty("size", out var s) ? s.GetInt64() : 0);

    /// <summary>
    /// Download every file into a fresh staging directory and verify each hash. Throws on
    /// any network or integrity failure (nothing is applied). Returns the staging path.
    /// </summary>
    public async Task<string> DownloadAsync(string serverUrl, UpdateInfo info,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var stage = Path.Combine(Path.GetTempPath(), "ftd-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);

        // Skip files already installed with a matching hash (e.g. the FFmpeg DLLs, which rarely
        // change) — only the actually-changed files get downloaded and staged for the swap.
        var installDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var toGet = new List<UpdateFile>();
        foreach (var f in info.Files)
        {
            var existing = Path.Combine(installDir, Path.GetFileName(f.Name));
            if (File.Exists(existing) &&
                (await Sha256Async(existing, ct)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;   // already current on disk
            toGet.Add(f);
        }

        long total = Math.Max(1, toGet.Sum(f => f.Size));
        long done = 0;
        foreach (var file in toGet)
        {
            var dest = Path.Combine(stage, Path.GetFileName(file.Name));
            using (var resp = await _http.GetAsync(UpdateBase + Uri.EscapeDataString(file.Name),
                       HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                var buf = new byte[64 * 1024];
                int r;
                while ((r = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, r), ct);
                    done += r;
                    progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            var actual = await Sha256Async(dest, ct);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(stage, true); } catch { }
                throw new IOException($"Checksum mismatch for {file.Name}.");
            }
        }
        return stage;
    }

    /// <summary>
    /// Launch the external swap-and-relaunch script, then return. The caller must shut the
    /// app down immediately afterward so the script (which waits on this PID) can replace
    /// the now-unlocked files. The relaunched app is the same exe that is running now.
    /// </summary>
    public void LaunchAndExit(string stageDir)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the running executable path.");
        var installDir = Path.GetDirectoryName(exePath)!;
        var exeName = Path.GetFileName(exePath);
        // The client shares its exe with the unattended worker; stopping the service frees
        // both it and RemotlerService.exe before the swap. Empty for the master.
        var serviceName = _component == "client" && File.Exists(Path.Combine(installDir, "RemotlerService.exe"))
            ? "RemotlerService" : "";

        var scriptPath = Path.Combine(Path.GetTempPath(), "ftd-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, SwapScript, new UTF8Encoding(false));

        // Elevate if asked, or if the install dir isn't writable (e.g. Program Files) — otherwise
        // the copy silently fails and the app relaunches the OLD exe, looking like "no update".
        bool elevate = _elevate || !IsWritable(installDir);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = elevate,           // RunAs requires shell execute
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) psi.Verb = "runas";
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle"); psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ProcId"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-InstallDir"); psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add("-StageDir"); psi.ArgumentList.Add(stageDir);
        psi.ArgumentList.Add("-ExeName"); psi.ArgumentList.Add(exeName);
        psi.ArgumentList.Add("-ServiceName"); psi.ArgumentList.Add(serviceName);

        Process.Start(psi);
    }

    // Wait for the app to exit, (optionally) stop the service so its files unlock, copy the
    // staged files over the install dir, restart the service, relaunch the app, clean up.
    private const string SwapScript = """
        param(
          [int]$ProcId, [string]$InstallDir, [string]$StageDir, [string]$ExeName, [string]$ServiceName
        )
        $ErrorActionPreference = 'SilentlyContinue'
        try { Wait-Process -Id $ProcId -Timeout 30 } catch {}
        if ($ServiceName) {
          & sc.exe stop $ServiceName | Out-Null
          for ($i = 0; $i -lt 20; $i++) {
            if ((& sc.exe query $ServiceName) -match 'STOPPED') { break }
            Start-Sleep -Milliseconds 500
          }
        }
        Start-Sleep -Milliseconds 300
        Get-ChildItem -Path $StageDir -File | ForEach-Object {
          Copy-Item $_.FullName (Join-Path $InstallDir $_.Name) -Force
        }
        if ($ServiceName) { & sc.exe start $ServiceName | Out-Null }
        Start-Process (Join-Path $InstallDir $ExeName)
        Remove-Item $StageDir -Recurse -Force
        Remove-Item $MyInvocation.MyCommand.Path -Force
        """;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    // Can we write into the install dir without elevation? Probe with a temp file.
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".ftd-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
