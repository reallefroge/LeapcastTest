namespace LeapcastStudioAI;

public enum StatusLevel { Off, Good, Warn, Bad }

public sealed record StatusItem(string Text, StatusLevel Level, string Tip);

/// <summary>The status bar (bottom right): short, OBS-style readouts computed from the engine's status.</summary>
public static class StatusSummary
{
    public static string Clock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static StatusItem Live(EngineStatus st) => st.Live
        ? new($"LIVE {Clock(st.LiveFor)}", st.Reconnecting ? StatusLevel.Warn : StatusLevel.Bad, "Stream uptime")
        : new($"LIVE {Clock(TimeSpan.Zero)}", StatusLevel.Off, "Not streaming");

    public static StatusItem Recording(EngineStatus st) => st.Recording
        ? new($"REC {Clock(st.RecordingFor)}", StatusLevel.Bad, "Recording time")
        : new($"REC {Clock(TimeSpan.Zero)}", StatusLevel.Off, "Not recording");

    /// <summary>The worst live destination: reconnecting/failed, or data queuing up because the upload can't keep up.</summary>
    public static StatusItem StreamHealth(EngineStatus st)
    {
        var outs = st.Outputs;
        if (!st.Live || outs.Count == 0) return new("Stream: offline", StatusLevel.Off, "Not streaming");
        if (outs.Any(o => o.State == OutputState.Error))
            return new("Stream: error", StatusLevel.Bad, string.Join("\n", outs.Where(o => o.State == OutputState.Error).Select(o => $"{o.Name}: {o.Message ?? "failed"}")));
        if (st.Reconnecting || outs.Any(o => o.State == OutputState.Reconnecting))
            return new("Stream: reconnecting", StatusLevel.Warn, string.Join("\n", outs.Where(o => o.State == OutputState.Reconnecting).Select(o => $"{o.Name}: reconnecting")));
        double queued = outs.Where(o => o.State == OutputState.Live).Select(o => o.BufferedSeconds).DefaultIfEmpty(0).Max();
        string tip = string.Join("\n", outs.Select(o => $"{o.Name}: {o.State}, {o.Kbps:N0} kbps, {o.BufferedSeconds:0.0} s queued"));
        return queued > 2.5 ? new("Stream: poor", StatusLevel.Bad, tip + "\nYour upload can't keep up. Lower the bitrate.")
             : queued > 0.8 ? new("Stream: fair", StatusLevel.Warn, tip)
             : new("Stream: good", StatusLevel.Good, tip);
    }

    /// <summary>What's being sent while live, and whether the upload keeps up with it (off when not live).</summary>
    public static StatusItem Bitrate(EngineStatus st)
    {
        if (st.Live && st.Outputs.Count > 0)
        {
            double queued = st.Outputs.Where(o => o.State == OutputState.Live).Select(o => o.BufferedSeconds).DefaultIfEmpty(0).Max();
            var level = queued > 2.5 ? StatusLevel.Bad : queued > 0.8 ? StatusLevel.Warn : StatusLevel.Good;
            string tip = $"Uploading {st.UploadKbps:N0} kbps in total (limit {st.CapKbps:N0} kbps video per destination).";
            if (level != StatusLevel.Good) tip += "\nData is queuing up: the connection is slower than the bitrate.";
            return new($"{st.UploadKbps:N0} kbps", level, tip);
        }
        return new("0 kbps", StatusLevel.Off, "Not live");
    }

    /// <summary>Encoding speed against real time: below 1× the PC can't keep up and frames are skipped.</summary>
    public static StatusItem Encoder(EngineStatus st, int targetFps)
    {
        // the stream's encoder: shown while live (recording alone doesn't light it up)
        if (!st.Running || !st.Live) return new("Encoder: idle", StatusLevel.Off, "Not live");
        string tip = $"Encoding at {st.Speed:0.00}× real time, {st.Fps:0.0} of {targetFps} FPS. Frames skipped: {st.Dropped:N0}.";
        if (st.Speed > 0 && st.Speed < 0.9)
            return new("Encoder overloaded", StatusLevel.Bad, tip + "\nLower the resolution or frame rate, or use a GPU encoder (Tools → Auto-config).");
        if (st.Speed > 0 && st.Speed < 0.97 || targetFps > 0 && st.Fps > 0 && st.Fps < targetFps * 0.9)
            return new("Encoder busy", StatusLevel.Warn, tip);
        return new("Encoder OK", StatusLevel.Good, tip);
    }

    public static StatusItem Fps(EngineStatus st, int targetFps) => st.Running && st.Fps > 0
        ? new($"{st.Fps:0.0} FPS", st.Fps >= targetFps * 0.95 ? StatusLevel.Good : st.Fps >= targetFps * 0.8 ? StatusLevel.Warn : StatusLevel.Bad, $"Target {targetFps} FPS")
        : new("— FPS", StatusLevel.Off, "");
}
