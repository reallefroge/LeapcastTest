using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI;

public sealed record ProgressInfo(long Frame, double Fps, long DroppedFrames, long DupFrames, double Speed, TimeSpan OutTime);

/// <summary>Runs one ffmpeg process: parses -progress output, reads raw preview frames from stdout, stops gracefully.</summary>
public sealed class FfmpegRunner : IDisposable
{
    private readonly Process _process;
    private readonly int _frameBytes;
    private readonly Queue<(string level, string line)> _log = new();
    private static readonly System.Text.RegularExpressions.Regex LevelTag = new(
        @"^(\[[^\]]* @ [^\]]*\] )?\[(trace|debug|verbose|info|warning|error|fatal|panic)\] ", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly object _logLock = new();
    private readonly StreamWriter? _logFile;
    private readonly Dictionary<string, string> _block = new();
    private volatile bool _stopRequested;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<ProgressInfo>? Progress;
    /// <summary>Raw BGRA frame. The array is reused: copy it before returning if you need it later.</summary>
    public event Action<byte[]>? Frame;
    public event Action<string>? LogLine;

    public bool StopRequested => _stopRequested;
    public Task<int> Completion => _exit.Task;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public int ProcessId { get; private set; }

    public FfmpegRunner(string ffmpeg, IReadOnlyList<string> args, int previewWidth, int previewHeight, string? logPath)
    {
        _frameBytes = previewWidth * previewHeight * 4;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (logPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                // Shared so the log can be opened (Settings → Open log folder) while streaming.
                var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _logFile = new StreamWriter(fs) { AutoFlush = true };
                _logFile.WriteLine("ffmpeg " + string.Join(" ", args.Select(Quote)));
                _logFile.WriteLine();
            }
            catch { _logFile = null; }
        }
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => { };
        _process.Start();
        try { ProcessId = _process.Id; } catch { }
        // Above-normal priority keeps the stream smooth when the game hogs the CPU (like OBS's process priority option).
        if (Environment.GetEnvironmentVariable("LEAPCAST_NO_PRIORITY_BOOST") != "1")
        {
            try { _process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { /* not fatal */ }
        }

        _ = Task.Run(ReadStderrAsync);
        _ = Task.Run(ReadStdoutAsync);
        _ = Task.Run(async () =>
        {
            await _process.WaitForExitAsync();
            await Task.Delay(150); // let the readers drain the last lines
            int code = -1;
            try { code = _process.ExitCode; } catch { }
            _exit.TrySetResult(code);
        });
    }

    public string TailLog(int lines = 12)
    {
        lock (_logLock) return string.Join(Environment.NewLine, _log.TakeLast(lines).Select(l => l.line));
    }

    /// <summary>Most relevant error line for showing to the user.</summary>
    public string ErrorSummary()
    {
        (string level, string line)[] all;
        lock (_logLock) all = _log.ToArray();
        all = all.Where(l => !l.line.Contains("Guessed Channel Layout", StringComparison.Ordinal)).ToArray();
        // tagged lines ("level+" logging): the last real error wins, then the last warning
        var tagged = all.Where(l => l.level.Length > 0).ToArray();
        if (tagged.Length > 0)
        {
            var err = tagged.LastOrDefault(l => l.level is "error" or "fatal" or "panic");
            if (err.line is not null) return err.line;
            var warn = tagged.LastOrDefault(l => l.level == "warning");
            if (warn.line is not null) return warn.line;
        }
        // untagged output (older logs, plain messages)
        var lines = all.Where(l => l.level is "" or "warning" or "error" or "fatal" or "panic").Select(l => l.line)
            .Where(l => !char.IsWhiteSpace(l.FirstOrDefault(' '))).ToArray();
        var hit = lines.Reverse().FirstOrDefault(l =>
            l.Contains("Error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Could not", StringComparison.OrdinalIgnoreCase) || l.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Invalid", StringComparison.OrdinalIgnoreCase) || l.Contains("denied", StringComparison.OrdinalIgnoreCase));
        return hit ?? "the encoder process stopped unexpectedly";
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                int eq = line.IndexOf('=');
                if (IsProgressLine(line, eq))
                {
                    var key = line[..eq];
                    var val = line[(eq + 1)..].Trim();
                    _block[key] = val;
                    if (key == "progress") EmitProgress();
                    continue;
                }
                bool commandEcho = line.StartsWith("Enter command", StringComparison.Ordinal)
                    || (line.StartsWith("Command reply", StringComparison.Ordinal) && line.Contains("ret:0", StringComparison.Ordinal));
                if (!commandEcho || Environment.GetEnvironmentVariable("LEAPCAST_LOG_COMMANDS") == "1") _logFile?.WriteLine(line);
                if (line.StartsWith("frame=", StringComparison.Ordinal) || line.Contains(" speed=", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*[a-z_]+=\s*\S*\s*$")
                    || string.IsNullOrWhiteSpace(line) || line.StartsWith("Enter command", StringComparison.Ordinal)
                    || (line.StartsWith("Command reply", StringComparison.Ordinal) && line.Contains("ret:0", StringComparison.Ordinal)))
                    continue; // stats summary / transition-command echoes, not messages
                string level = "";
                var m = LevelTag.Match(line);
                if (m.Success)
                {
                    level = m.Groups[2].Value;
                    line = m.Groups[1].Value + line[m.Length..]; // keep "[context @ …] ", drop "[level] "
                }
                lock (_logLock)
                {
                    _log.Enqueue((level, line));
                    while (_log.Count > 200) _log.Dequeue();
                }
                LogLine?.Invoke(line);
            }
        }
        catch { /* process gone */ }
    }

    /// <summary>-progress lines are "key=value" with a lower-case key and no spaces anywhere.</summary>
    internal static bool IsProgressLine(string line, int eq)
    {
        if (eq <= 0 || eq > 40 || line.IndexOf(' ') >= 0) return false;
        for (int i = 0; i < eq; i++)
        {
            char c = line[i];
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        }
        return true;
    }

    private void EmitProgress()
    {
        long L(string k) => _block.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
        double D(string k)
        {
            if (!_block.TryGetValue(k, out var v)) return 0;
            v = v.TrimEnd('x');
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
        }
        var info = new ProgressInfo(L("frame"), D("fps"), L("drop_frames"), L("dup_frames"), D("speed"),
            TimeSpan.FromMilliseconds(Math.Max(0, L("out_time_us") / 1000)));
        try { Progress?.Invoke(info); } catch { }
    }

    private async Task ReadStdoutAsync()
    {
        if (_frameBytes <= 0) return;
        var stream = _process.StandardOutput.BaseStream;
        var buffers = new[] { new byte[_frameBytes], new byte[_frameBytes] };
        int which = 0;
        try
        {
            while (true)
            {
                var buf = buffers[which];
                int got = 0;
                while (got < _frameBytes)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(got, _frameBytes - got));
                    if (n <= 0) return;
                    got += n;
                }
                try { Frame?.Invoke(buf); } catch { }
                which ^= 1;
            }
        }
        catch { /* process gone */ }
    }

    private readonly object _stdinLock = new();

    /// <summary>Sends runtime filter commands ("target time command arg") through ffmpeg's interactive stdin.</summary>
    public void SendCommands(IEnumerable<string> commands)
    {
        if (_exit.Task.IsCompleted || _stopRequested) return;
        lock (_stdinLock)
        {
            try
            {
                foreach (var c in commands) _process.StandardInput.Write("C" + c + "\n");
                _process.StandardInput.Flush();
            }
            catch { /* process exiting */ }
        }
    }

    /// <summary>Asks ffmpeg to finish cleanly ('q'), so RTMP is closed properly and the last segment is finalized.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _stopRequested = true;
        if (_exit.Task.IsCompleted) return;
        try { lock (_stdinLock) { _process.StandardInput.Write("q"); _process.StandardInput.Flush(); } } catch { }
        var done = await Task.WhenAny(_exit.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(6)));
        if (done != _exit.Task)
        {
            try { _process.Kill(true); } catch { }
            await Task.WhenAny(_exit.Task, Task.Delay(2000));
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        _process.Dispose();
        _logFile?.Dispose();
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
}
