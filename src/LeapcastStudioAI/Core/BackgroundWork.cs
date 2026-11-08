using System.Diagnostics;

namespace LeapcastStudioAI;

/// <summary>
/// Heavy work that isn't the stream (the VOD editor's FFmpeg and Whisper runs): full speed when you're not live;
/// while you stream or record it drops to idle priority and a couple of threads, so it can never make the stream
/// stutter. Processes already running are moved down (or back up) the moment that changes.
/// </summary>
public static class BackgroundWork
{
    private static readonly List<WeakReference<Process>> _running = new();
    private static volatile bool _live;

    /// <summary>Streaming or recording right now (set by the main window).</summary>
    public static bool Live
    {
        get => _live;
        set
        {
            if (_live == value) return;
            _live = value;
            lock (_running)
            {
                _running.RemoveAll(w => !w.TryGetTarget(out var p) || HasExited(p));
                foreach (var w in _running) if (w.TryGetTarget(out var p)) SetPriority(p);
            }
        }
    }

    public static ProcessPriorityClass Priority => _live ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;

    /// <summary>Threads for one heavy process.</summary>
    public static int Threads => _live ? 2 : Math.Clamp(Environment.ProcessorCount - 2, 2, 12);

    /// <summary>How many FFmpeg renders at once.</summary>
    public static int Parallel => _live ? 1 : Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    /// <summary>Gives a just-started process the right priority, and keeps adjusting it.</summary>
    public static void Track(Process p)
    {
        SetPriority(p);
        lock (_running)
        {
            _running.RemoveAll(w => !w.TryGetTarget(out var x) || HasExited(x));
            _running.Add(new WeakReference<Process>(p));
        }
    }

    private static void SetPriority(Process p) { try { if (!p.HasExited) p.PriorityClass = Priority; } catch { } }
    private static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }
}
