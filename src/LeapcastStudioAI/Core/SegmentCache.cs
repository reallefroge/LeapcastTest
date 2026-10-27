using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>
/// The encoded stream is also written as short MPEG-TS segments. That one encode then serves
/// three jobs without extra GPU/CPU work: instant replay (keep the last N seconds), recording
/// (keep everything between Start and Stop, can begin/end at any time) and exact bitrate stats.
/// </summary>
public sealed class SegmentCache
{
    public sealed record Segment(string Path, int Session, int Number, long Size, DateTime WrittenUtc, bool Closed)
    {
        public long Key => ((long)Session << 32) | (uint)Number;
    }

    private readonly string _root;
    private readonly object _lock = new();
    private int _sessionCounter;
    private string? _activeDir;
    private int _activeSession = -1;
    private readonly HashSet<int> _endedSessions = new();
    private long? _recordingFrom;
    private readonly List<(long from, long to)> _pinned = new();

    public int SegmentSeconds { get; }
    public bool IsRecording { get { lock (_lock) return _recordingFrom is not null; } }
    public DateTime? RecordingStartedUtc { get; private set; }

    public SegmentCache(string root, int segmentSeconds = 2)
    {
        _root = root;
        SegmentSeconds = segmentSeconds;
        // Leftovers from a crash are useless; start clean.
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        Directory.CreateDirectory(_root);
    }

    /// <summary>Starts a new segment session and returns the ffmpeg output pattern for it.</summary>
    public string BeginSession()
    {
        lock (_lock)
        {
            _activeSession = ++_sessionCounter;
            _activeDir = System.IO.Path.Combine(_root, "s" + _activeSession.ToString("0000", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_activeDir);
            return System.IO.Path.Combine(_activeDir, "seg_%06d.ts").Replace('\\', '/');
        }
    }

    public void EndSession()
    {
        lock (_lock)
        {
            if (_activeSession > 0) _endedSessions.Add(_activeSession);
            _activeSession = -1;
            _activeDir = null;
        }
    }

    public bool HasActiveSession { get { lock (_lock) return _activeSession > 0; } }

    public List<Segment> Scan()
    {
        var result = new List<Segment>();
        if (!Directory.Exists(_root)) return result;
        HashSet<int> ended;
        lock (_lock) ended = new HashSet<int>(_endedSessions);
        foreach (var dir in Directory.GetDirectories(_root, "s*"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir)[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)) continue;
            var files = new List<(int n, FileInfo f)>();
            foreach (var path in Directory.GetFiles(dir, "seg_*.ts"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    files.Add((n, new FileInfo(path)));
            }
            files.Sort((a, b) => a.n.CompareTo(b.n));
            for (int i = 0; i < files.Count; i++)
            {
                bool closed = i < files.Count - 1 || ended.Contains(session);
                var f = files[i].f;
                long size = 0; DateTime written = DateTime.UtcNow;
                try { f.Refresh(); size = f.Length; written = f.LastWriteTimeUtc; } catch { }
                result.Add(new Segment(f.FullName, session, files[i].n, size, written, closed));
            }
        }
        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        return result;
    }

    private long CurrentKey()
    {
        var segs = Scan();
        lock (_lock)
        {
            var active = segs.LastOrDefault(s => s.Session == _activeSession);
            if (active is not null) return active.Key;
            if (_activeSession > 0) return ((long)_activeSession << 32);
            // nothing running: the next session will start at segment 0
            return ((long)(_sessionCounter + 1) << 32);
        }
    }

    public void StartRecording()
    {
        long from = CurrentKey();
        lock (_lock) { _recordingFrom = from; RecordingStartedUtc = DateTime.UtcNow; }
    }

    /// <summary>Stops recording and writes the MP4. Returns the output path.</summary>
    public async Task<string> StopRecordingAsync(string ffmpeg, string outputPath, CancellationToken ct = default)
    {
        long from;
        lock (_lock)
        {
            if (_recordingFrom is null) throw new InvalidOperationException("Not recording.");
            from = _recordingFrom.Value;
        }
        long to = LastExistingKey();
        lock (_lock) { _pinned.Add((from, to)); _recordingFrom = null; RecordingStartedUtc = null; }
        try
        {
            await WaitClosedAsync(to, TimeSpan.FromSeconds(SegmentSeconds * 4 + 4), ct);
            var segs = Scan().Where(s => s.Closed && s.Key >= from && s.Key <= to && s.Size > 0).ToList();
            if (segs.Count == 0) throw new InvalidOperationException("Nothing was recorded (the encoder never started).");
            await ConcatAsync(ffmpeg, segs, outputPath, ct);
            return outputPath;
        }
        finally { lock (_lock) _pinned.Remove((from, to)); }
    }

    /// <summary>Saves the last <paramref name="seconds"/> of the stream as an MP4.</summary>
    public async Task<string> SaveReplayAsync(string ffmpeg, int seconds, string outputPath, CancellationToken ct = default)
    {
        long to = LastExistingKey();
        lock (_lock) _pinned.Add((long.MinValue, to));
        try
        {
            await WaitClosedAsync(to, TimeSpan.FromSeconds(SegmentSeconds * 4 + 4), ct);
            var closed = Scan().Where(s => s.Closed && s.Key <= to && s.Size > 0).ToList();
            int need = Math.Max(1, (int)Math.Ceiling(seconds / (double)SegmentSeconds));
            var segs = closed.Skip(Math.Max(0, closed.Count - need)).ToList();
            if (segs.Count == 0) throw new InvalidOperationException("The replay buffer is empty. It fills while you are live, recording, or have the replay buffer on.");
            await ConcatAsync(ffmpeg, segs, outputPath, ct);
            return outputPath;
        }
        finally { lock (_lock) _pinned.Remove((long.MinValue, to)); }
    }

    private long LastExistingKey()
    {
        var segs = Scan();
        return segs.Count == 0 ? long.MinValue : segs[^1].Key;
    }

    private async Task WaitClosedAsync(long key, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var seg = Scan().FirstOrDefault(s => s.Key == key);
            if (seg is null || seg.Closed) return;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Deletes segments that are older than the replay window and not needed by a recording/export.</summary>
    public void Housekeep(int replaySeconds)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(replaySeconds + SegmentSeconds * 3 + 5);
        long? rec; List<(long from, long to)> pins;
        lock (_lock) { rec = _recordingFrom; pins = _pinned.ToList(); }
        foreach (var s in Scan())
        {
            if (!s.Closed || s.WrittenUtc >= cutoff) continue;
            if (rec is not null && s.Key >= rec) continue;
            if (pins.Any(p => s.Key >= p.from && s.Key <= p.to)) continue;
            try { File.Delete(s.Path); } catch { }
        }
        // remove empty finished session folders
        lock (_lock)
        {
            foreach (var dir in Directory.GetDirectories(_root, "s*"))
            {
                try
                {
                    if (dir != _activeDir && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
                }
                catch { }
            }
        }
    }

    /// <summary>Real encoded bitrate (video + audio) over the most recent closed segments.</summary>
    public double RecentKbps(int segmentsToAverage = 5)
    {
        var closed = Scan().Where(s => s.Closed && s.Size > 0).ToList();
        lock (_lock) closed = closed.Where(s => s.Session == _activeSession).ToList();
        if (closed.Count == 0) return 0;
        var last = closed.Skip(Math.Max(0, closed.Count - segmentsToAverage)).ToList();
        return last.Sum(s => s.Size) * 8.0 / 1000.0 / (last.Count * SegmentSeconds);
    }

    public static async Task ConcatAsync(string ffmpeg, IReadOnlyList<Segment> segs, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
        string list = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leapcast-concat-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, segs.Select(s => "file '" + s.Path.Replace('\\', '/').Replace("'", "'\\''") + "'"), ct);
        try
        {
            var args = new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list,
                "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", outputPath };
            var (code, err) = await EncoderCatalog.RunQuietAsync(ffmpeg, args, TimeSpan.FromMinutes(30), ct);
            if (code != 0) throw new InvalidOperationException("Could not write the video file: " + err.Trim());
        }
        finally { try { File.Delete(list); } catch { } }
    }
}
