namespace LeapcastStudioAI;

/// <summary>How much headroom this PC has for capturing, compositing and encoding next to a game.</summary>
public enum HardwareTier { Low, Mid, High }

public static class HardwareProfile
{
    /// <summary>
    /// Low: ≤ 4 CPU threads, under 6 GB of RAM, or CPU encoding on ≤ 8 threads (the game and the encoder fight
    /// over the same cores). High: 12+ threads, 16 GB+, and a modern GPU encoder. Everything else is Mid.
    /// </summary>
    public static HardwareTier Classify(int cpuThreads, long memoryBytes, IReadOnlyList<ProbedEncoder> encoders)
    {
        bool gpuEncoder = encoders.Any(e => e.Name != "libx264" && e.Tier == EncoderTier.Rich);
        double gb = memoryBytes / (1024.0 * 1024 * 1024);
        if (cpuThreads <= 4 || (gb > 0 && gb < 6) || (!gpuEncoder && cpuThreads <= 8)) return HardwareTier.Low;
        if (cpuThreads >= 12 && (gb <= 0 || gb >= 15) && gpuEncoder) return HardwareTier.High;
        return HardwareTier.Mid;
    }

    /// <summary>The lightest the governor starts at on this hardware (it can still step down further, or back up).</summary>
    public static int StartLevel(HardwareTier tier) => tier == HardwareTier.Low ? 2 : 0;
}

/// <summary>One rung of the performance ladder. Each rung keeps everything the rungs above it changed.</summary>
public sealed record PerfLevel(int Index, string Name, string Explanation, bool LightEncoder, int PreviewFps,
    bool SmallPreview, int MaxFps, bool ActiveSceneOnly, double Scale)
{
    /// <summary>Cheapest-to-notice first: viewers barely see the first rungs; picture size is the last resort.</summary>
    public static readonly PerfLevel[] Ladder =
    {
        new(0, "Full quality", "as configured", false, 30, false, 240, false, 1.0),
        new(1, "Light encoder", "faster encoder settings and a lighter preview", true, 15, true, 240, false, 1.0),
        new(2, "30 FPS", "30 frames per second", true, 15, true, 30, false, 1.0),
        new(3, "Active scene only", "only the scene on air is captured (switching scenes takes a second)", true, 15, true, 30, true, 1.0),
        new(4, "Smaller picture", "a smaller picture", true, 10, true, 30, true, 0.8),
        new(5, "Smallest picture", "the smallest picture", true, 10, true, 30, true, 0.64),
    };

    public static PerfLevel At(int i) => Ladder[Math.Clamp(i, 0, Ladder.Length - 1)];

    /// <summary>The plan this rung allows (frame rate cap and picture size; never below 640×360).</summary>
    public EncodePlan Apply(EncodePlan plan)
    {
        var p = plan;
        if (p.Fps > MaxFps) p = p with { Fps = MaxFps };
        if (Scale < 0.999)
        {
            int w = SmartTuner.Even(Math.Max(640, (int)Math.Round(p.Width * Scale)));
            int h = SmartTuner.Even(Math.Max(360, (int)Math.Round(w * p.Height / (double)p.Width)));
            if (w < p.Width) p = p with { Width = w, Height = h };
        }
        return LightEncoder ? p with { Light = true } : p;
    }
}

/// <summary>
/// Keeps the pipeline ahead of real time on any PC. Every second it looks at three warning signs (a fourth, silence, is the engine's stall watchdog):
/// <list type="bullet">
/// <item>speed: output seconds per real second (below 1 = the PC can't keep up; viewers see stutter),</item>
/// <item>backlog: frames waiting in front of the compositor (grows before stutter is visible, and is camera delay),</item>
/// <item>upload: the deepest per-destination send queue (a slow connection, not a slow PC).</item>
/// </list>
/// A PC that falls behind is stepped down the <see cref="PerfLevel.Ladder"/> (fast for heavy lag, slower for mild);
/// after a long healthy stretch, and only while nothing is live or recording, it is stepped back up, skipping any
/// rung that failed recently, so it never flaps. Pure logic: the engine applies the decisions.
/// </summary>
public sealed class PerformanceGovernor
{
    /// <param name="FpsRatio">Frames out per second ÷ the frame rate over the same window (0 = unknown). At or above
    /// 1 the PC is keeping up (after a start it's catching up), whatever the speed reading says.</param>
    public sealed record Sample(double Speed, double BacklogSeconds, double UploadBacklogSeconds, TimeSpan SinceStart, double FpsRatio = 0);

    public enum Action { None, StepDown, StepUp, LowerBitrate }

    public sealed record Decision(Action Action, int Level, string Reason);

    // thresholds (seconds of evidence before acting)
    /// <summary>The first seconds after a start are catch-up (frames queued while FFmpeg opened its inputs): not judged.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SevereFor = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MildFor = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HealthyFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(10);

    private DateTime? _badSince, _severeSince, _healthySince, _uploadSince;
    private DateTime _lastChange = DateTime.MinValue, _lastBitrate = DateTime.MinValue;
    /// <summary>When the rung last changed.</summary>
    public DateTime LastChange => _lastChange;
    private readonly Dictionary<int, DateTime> _failed = new();

    /// <summary>Current rung of <see cref="PerfLevel.Ladder"/>.</summary>
    public int Level { get; private set; }
    /// <summary>Where this PC starts (and the governor never steps up above).</summary>
    public int Floor { get; private set; }
    /// <summary>Upload bitrate multiplier (1 = as planned), lowered when a destination can't keep up.</summary>
    public double BitrateScale { get; private set; } = 1.0;
    /// <summary>Latest condition, for the status display.</summary>
    public string Condition { get; private set; } = "Idle";

    /// <summary>New settings from the user, or a new session: start over from the hardware's starting rung.</summary>
    public void Reset(int floor)
    {
        Floor = Math.Clamp(floor, 0, PerfLevel.Ladder.Length - 1);
        Level = Floor;
        BitrateScale = 1.0;
        _failed.Clear();
        _badSince = _severeSince = _healthySince = _uploadSince = null;
        _lastChange = DateTime.MinValue;
    }

    /// <summary>A new stream starts with the full planned bitrate.</summary>
    public void ResetBitrate() { BitrateScale = 1.0; _uploadSince = null; }

    /// <summary>Tests / diagnostics: jump to a rung.</summary>
    public void Force(int level) { Level = Math.Clamp(level, 0, PerfLevel.Ladder.Length - 1); _lastChange = DateTime.UtcNow; }

    /// <param name="useful">Does this rung change anything for the current scene/plan (e.g. 30 FPS when already at 30)?</param>
    /// <param name="mayStepUp">Stepping up rebuilds the pipeline: only allowed when nothing is live or recording.</param>
    /// <param name="adaptBitrate">The user allows automatic bitrate reduction.</param>
    public Decision Evaluate(Sample s, DateTime now, Func<int, bool> useful, bool mayStepUp, bool adaptBitrate)
    {
        // ---- network: a destination whose queue keeps growing gets less bitrate (all share one encode) ----
        if (s.UploadBacklogSeconds > 2.5)
        {
            _uploadSince ??= now;
            Condition = "Upload struggling";
            if (adaptBitrate && now - _uploadSince > TimeSpan.FromSeconds(10) && now - _lastBitrate > TimeSpan.FromSeconds(60) && BitrateScale > 0.35)
            {
                _lastBitrate = now;
                _uploadSince = null;
                BitrateScale *= 0.75;
                return new Decision(Action.LowerBitrate, Level, "your upload couldn't keep up");
            }
        }
        else _uploadSince = null;

        if (s.SinceStart < Warmup) return new Decision(Action.None, Level, "warming up");

        // ---- PC load ----
        // frames coming out at the full rate (or faster: catching up) is never "can't keep up", even when the
        // speed reading lags behind (it did at a start: 35 fps out of 30 read as speed 0.86 → a needless step down)
        bool keepingUp = s.FpsRatio >= 0.98;
        bool slow = !keepingUp && s.Speed > 0;
        bool severe = (slow && s.Speed < 0.85) || s.BacklogSeconds > 1.0;
        bool mild = severe || (slow && s.Speed < 0.95) || s.BacklogSeconds > 0.4;
        if (mild)
        {
            _healthySince = null;
            _badSince ??= now;
            if (severe) _severeSince ??= now; else _severeSince = null;
            if (s.UploadBacklogSeconds <= 2.5)
                Condition = severe ? "Struggling: this PC can't keep up" : "Falling behind slightly";
            bool act = (_severeSince is { } sv && now - sv >= SevereFor) || now - _badSince >= MildFor;
            if (act && now - _lastChange >= Cooldown)
            {
                int next = NextUseful(Level, +1, useful);
                if (next != Level)
                {
                    _failed[Level] = now;
                    Level = next;
                    _lastChange = now;
                    _badSince = _severeSince = null;
                    return new Decision(Action.StepDown, Level, severe ? "this PC fell well behind real time" : "this PC kept falling behind real time");
                }
                Condition = "Struggling: already at the lightest settings";
            }
            return new Decision(Action.None, Level, Condition);
        }

        _badSince = _severeSince = null;
        bool healthy = (s.Speed <= 0 || s.Speed >= 0.99) && s.BacklogSeconds < 0.1;
        if (!healthy) { _healthySince = null; if (s.UploadBacklogSeconds <= 2.5) Condition = "Running"; return new Decision(Action.None, Level, Condition); }
        _healthySince ??= now;
        if (s.UploadBacklogSeconds <= 2.5) Condition = Level > Floor ? $"Smooth ({PerfLevel.At(Level).Name})" : "Smooth";

        // ---- step back up after a long healthy stretch, never into a rung that failed recently ----
        if (mayStepUp && Level > Floor && now - _healthySince >= HealthyFor && now - _lastChange >= HealthyFor)
        {
            int up = NextUseful(Level, -1, useful);
            if (up != Level && up >= Floor && !(_failed.TryGetValue(up, out var f) && now - f < FailureMemory))
            {
                Level = up;
                _lastChange = now;
                _healthySince = null;
                return new Decision(Action.StepUp, Level, "this PC has had headroom for a while");
            }
        }
        return new Decision(Action.None, Level, Condition);
    }

    private static int NextUseful(int from, int dir, Func<int, bool> useful)
    {
        for (int i = from + dir; i >= 0 && i < PerfLevel.Ladder.Length; i += dir)
            if (useful(i)) return i;
        return from;
    }
}
