using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace RemoteDesktop.Client;

/// <summary>
/// Executes admin commands the relay forwards to this host over signaling (task manager,
/// kill, Wake-on-LAN, config apply). Each returns a mutable result dict; HostSession adds
/// the <c>t</c>/<c>reqId</c> envelope and sends it back. Runs synchronously and fast.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HostCommands
{
    public static Dictionary<string, object?> Handle(string? cmd, JsonElement args)
    {
        try
        {
            return cmd switch
            {
                "tasklist" => TaskList(),
                "kill" => Kill(args.GetProperty("pid").GetInt32()),
                "wol" => WakeOnLan.Send(args.TryGetProperty("mac", out var m) ? m.GetString() : null),
                _ => Err("unknown command"),
            };
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static Dictionary<string, object?> TaskList()
    {
        var tasks = Process.GetProcesses()
            .Select(p =>
            {
                try { return new { pid = p.Id, name = p.ProcessName, mem = p.WorkingSet64 }; }
                catch { return null; }
                finally { p.Dispose(); }
            })
            .Where(x => x is { name.Length: > 0 })
            .OrderByDescending(x => x!.mem)
            .Take(300)
            .Select(x => (object)new { x!.pid, x.name, x.mem })
            .ToList();
        return Ok(new() { ["tasks"] = tasks });
    }

    private static Dictionary<string, object?> Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    internal static Dictionary<string, object?> Ok(Dictionary<string, object?>? extra = null)
    {
        var d = extra ?? new();
        d["ok"] = true;
        return d;
    }

    internal static Dictionary<string, object?> Err(string msg) => new() { ["ok"] = false, ["error"] = msg };
}
