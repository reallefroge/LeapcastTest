using System.Diagnostics;

namespace LeapcastStudioAI;

/// <summary>
/// Where the CPU and memory go: Leapcast itself, the encoder, capture readers, destination senders and browser
/// sources (each runs as its own process). Sampled every couple of seconds for the status bar.
/// </summary>
public static class ResourceMonitor
{
    public const string App = "Leapcast", Encoder = "Encoder", Capture = "Capture", Destinations = "Destinations", Browser = "Browser sources";

    private static readonly object Gate = new();
    private static readonly List<(Process Process, string Role)> Tracked = new();
    private static readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> Last = new();

    /// <summary>Process ids of browser sources (WebView2 runs them in its own processes); set by the browser host.</summary>
    public static Func<IEnumerable<int>>? BrowserProcessIds { get; set; }

    /// <summary>Counts a process Leapcast started under a role, until it exits.</summary>
    public static void Track(Process? p, string role)
    {
        if (p is null) return;
        lock (Gate)
        {
            Tracked.RemoveAll(t => HasExited(t.Process));
            Tracked.Add((p, role));
        }
    }

    public sealed record Usage(string Role, double CpuPercent, long MemoryBytes, int Processes);

    /// <summary>
    /// The last sample and when it was taken. CPU is a delta since the previous <see cref="Sample"/>, so a
    /// second caller sampling on its own clock would shorten everyone's window and report the wrong figure:
    /// anything that only wants the numbers reads this instead of sampling again.
    /// </summary>
    public static (DateTime At, IReadOnlyList<Usage> Usage) Latest { get; private set; } = (DateTime.MinValue, Array.Empty<Usage>());

    /// <summary>The last sample while it is still fresh, otherwise a new one (nobody else is sampling).</summary>
    public static IReadOnlyList<Usage> Recent(TimeSpan freshFor)
    {
        var (at, usage) = Latest;
        return DateTime.UtcNow - at <= freshFor && usage.Count > 0 ? usage : Sample();
    }

    /// <summary>CPU (% of the whole PC) and memory per role since the previous call.</summary>
    public static List<Usage> Sample()
    {
        var now = DateTime.UtcNow;
        var groups = new Dictionary<string, (double Cpu, long Mem, int N)>();
        void Add(string role, Process p)
        {
            try
            {
                if (p.HasExited) return;
                p.Refresh();
                var cpu = p.TotalProcessorTime;
                double pct = 0;
                if (Last.TryGetValue(p.Id, out var prev) && now > prev.At)
                    pct = (cpu - prev.Cpu).TotalMilliseconds / (now - prev.At).TotalMilliseconds / Environment.ProcessorCount * 100;
                Last[p.Id] = (cpu, now);
                var g = groups.TryGetValue(role, out var v) ? v : (0, 0, 0);
                groups[role] = (g.Cpu + Math.Max(0, pct), g.Mem + p.WorkingSet64, g.N + 1);
            }
            catch { /* exited or not accessible */ }
        }
        lock (Gate)
        {
            Add(App, Process.GetCurrentProcess());
            Tracked.RemoveAll(t => HasExited(t.Process));
            foreach (var (p, role) in Tracked) Add(role, p);
            try
            {
                foreach (var id in BrowserProcessIds?.Invoke() ?? Array.Empty<int>())
                    try { using var p = Process.GetProcessById(id); Add(Browser, p); } catch { }
            }
            catch { }
            if (Last.Count > 500) Last.Clear();
        }
        string[] order = { App, Encoder, Capture, Destinations, Browser };
        var list = groups.Select(kv => new Usage(kv.Key, kv.Value.Cpu, kv.Value.Mem, kv.Value.N))
            .OrderBy(u => Array.IndexOf(order, u.Role) is var i && i < 0 ? 99 : i).ToList();
        Latest = (now, list);
        return list;
    }

    private static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    public static string Describe(IReadOnlyList<Usage> usage)
    {
        double cpu = usage.Sum(u => u.CpuPercent);
        long mem = usage.Sum(u => u.MemoryBytes);
        var lines = usage.Select(u => $"{u.Role}: {u.CpuPercent:0.0}% CPU · {u.MemoryBytes / 1048576.0:N0} MB" + (u.Processes > 1 ? $" ({u.Processes} processes)" : ""));
        return $"Total: {cpu:0.0}% CPU · {mem / 1048576.0:N0} MB\n" + string.Join("\n", lines);
    }
}
