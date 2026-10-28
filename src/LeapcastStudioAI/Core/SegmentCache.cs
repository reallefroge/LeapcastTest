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
            _scanCache = null;
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
            _scanCache = null;
        }
    }

    public bool HasActiveSession { get { lock (_lock) return _activeSession > 0; } }

    private List<Segment>? _scanCache;
    private DateTime _scanAt;
    /// <summary>Closed segments never change: their size/time is read once (a long recording has thousands).</summary>
    private readonly Dictionary<string, (long size, DateTime written)> _closedInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All segments in order. Results are reused for 250 ms (the monitor, stats and housekeeping share one scan).</summary>
    public List<Segment> Scan()
    {
        lock (_lock)
        {
            if (_scanCache is not null && DateTime.UtcNow - _scanAt < TimeSpan.FromMilliseconds(250)) return new List<Segment>(_scanCache);
        }
        var result = new List<Segment>();
        if (!Directory.Exists(_root)) return result;
        HashSet<int> ended;
        lock (_lock) ended = new HashSet<int>(_endedSessions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(_root, "s*"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir)[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)) continue;
            var files = new List<(int n, FileInfo f)>();
            try
            {
                // DirectoryInfo enumeration carries size + time from the directory listing: no per-file stat call
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("seg_*.ts"))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    if (int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        files.Add((n, f));
                }
            }
            catch { continue; }
            files.Sort((a, b) => a.n.CompareTo(b.n));
            for (int i = 0; i < files.Count; i++)
            {
                bool closed = i < files.Count - 1 || ended.Contains(session);
                var f = files[i].f;
                string path = f.FullName;
                seen.Add(path);
                long size = 0; DateTime written = DateTime.UtcNow;
                bool cached;
                (long size, DateTime written) info;
                lock (_lock) cached = _closedInfo.TryGetValue(path, out info);
                if (cached) { size = info.size; written = info.written; }
                else
                {
                    try
                    {
                        if (!closed) f.Refresh(); // the segment being written: the listing may lag behind
                        size = f.Length; written = f.LastWriteTimeUtc;
                    }
                    catch { }
                    if (closed && size > 0) lock (_lock) _closedInfo[path] = (size, written);
                }
                result.Add(new Segment(path, session, files[i].n, size, written, closed));
            }
        }
        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        lock (_lock)
        {
            if (_closedInfo.Count > seen.Count + 64)
                foreach (var k in _closedInfo.Keys.Where(k => !seen.Contains(k)).ToList()) _closedInfo.Remove(k);
            _scanCache = result;
            _scanAt = DateTime.UtcNow;
        }
        return new List<Segment>(result);
    }

    /// <summary>Forget the memoized scan (after deleting files or ending a session).</summary>
    private void InvalidateScan() { lock (_lock) _scanCache = null; }

    private long CurrentKey()
    {
        InvalidateScan();
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

    /// <summary>Remembers "now" so <see cref="ExportSinceAsync"/> can export exactly what is recorded from here on.</summary>
    public long Mark()
    {
        long key = CurrentKey();
        lock (_lock) _pinned.Add((key, long.MaxValue));
        return key;
    }

    /// <summary>Exports every segment from <paramref name="fromKey"/> (a <see cref="Mark"/>) up to now, then releases the mark.</summary>
    public async Task<string> ExportSinceAsync(string ffmpeg, long fromKey, string outputPath, CancellationToken ct = default)
    {
        long to = LastExistingKey();
        try
        {
            await WaitClosedAsync(to, TimeSpan.FromSeconds(SegmentSeconds * 4 + 4), ct);
            InvalidateScan();
            var segs = Scan().Where(s => s.Closed && s.Key >= fromKey && s.Key <= to && s.Size > 0).ToList();
            if (segs.Count == 0) throw new InvalidOperationException("Nothing was recorded (the encoder never started).");
            await ConcatAsync(ffmpeg, segs, outputPath, ct);
            return outputPath;
        }
        finally { Release(fromKey); }
    }

    /// <summary>Drops a <see cref="Mark"/> (its segments may be housekept again).</summary>
    public void Release(long fromKey) { lock (_lock) _pinned.RemoveAll(p => p.from == fromKey && p.to == long.MaxValue); }

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
        InvalidateScan();
        var segs = Scan();
        return segs.Count == 0 ? long.MinValue : segs[^1].Key;
    }

    private async Task WaitClosedAsync(long key, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            InvalidateScan();
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
        bool deleted = false;
        foreach (var s in Scan())
        {
            if (!s.Closed || s.WrittenUtc >= cutoff) continue;
            if (rec is not null && s.Key >= rec) continue;
            if (pins.Any(p => s.Key >= p.from && s.Key <= p.to)) continue;
            try { File.Delete(s.Path); deleted = true; } catch { }
        }
        if (deleted) InvalidateScan();
        // remove empty finished session folders
        lock (_lock)
        {
            foreach (var dir in Directory.GetDirectories(_root, "s*"))
            {
                try
                {
                    if (dir != _activeDir && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
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

    /// <summary>
    /// Joins segments into an MP4 without touching timestamps: segments of one encoder session are appended
    /// byte-for-byte (MPEG-TS is designed for that, and the segment muxer kept continuous timestamps), then sessions
    /// are joined and remuxed. Re-timing every 2 s segment (concat demuxer) leaves tiny audio gaps that MP4 turns
    /// into progressive A/V drift.
    /// </summary>
    public static async Task ConcatAsync(string ffmpeg, IReadOnlyList<Segment> segs, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
        string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leapcast-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var sessionFiles = new List<string>();
            foreach (var group in segs.GroupBy(x => x.Session).OrderBy(g => g.Key))
            {
                string joined = System.IO.Path.Combine(work, $"session_{group.Key:0000}.ts");
                await using (var outStream = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                {
                    foreach (var seg in group.OrderBy(x => x.Number))
                    {
                        await using var inStream = new FileStream(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
                        await inStream.CopyToAsync(outStream, ct);
                    }
                }
                sessionFiles.Add(joined);
            }

            List<string> args;
            if (sessionFiles.Count == 1)
                args = new() { "-hide_banner", "-loglevel", "error", "-y", "-i", sessionFiles[0] };
            else
            {
                // pipeline restarts during a recording: join the (few) sessions end to end
                string list = System.IO.Path.Combine(work, "sessions.txt");
                await File.WriteAllLinesAsync(list, sessionFiles.Select(f => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'"), ct);
                args = new() { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list };
            }
            args.AddRange(new[] { "-map", "0", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", outputPath });
            var (code, err) = await EncoderCatalog.RunQuietAsync(ffmpeg, args, TimeSpan.FromMinutes(30), ct);
            if (code != 0) throw new InvalidOperationException("Could not write the video file: " + err.Trim());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
