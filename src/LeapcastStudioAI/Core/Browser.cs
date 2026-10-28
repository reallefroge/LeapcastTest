namespace LeapcastStudioAI;

/// <summary>A live picture produced outside ffmpeg (a web page), as BGRA frames with transparency.</summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>Newest frame (Width × Height × 4 bytes, BGRA, straight alpha), or null before the first one.</summary>
    byte[]? Latest();
    /// <summary>Why the source can't produce pictures (shown to the user), or null.</summary>
    string? Error { get; }
}

/// <summary>
/// Keeps each browser source rendering (pages stay loaded across pipeline restarts, so chat history isn't lost)
/// and feeds it to the pipeline through a <see cref="CountedFramePipe"/> at the source's own, low frame rate.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    private readonly Dictionary<string, IFrameSource> _sources = new();
    private readonly List<CountedFramePipe> _pipes = new();
    private readonly string _scratch;
    private readonly Func<StudioSource, IFrameSource?> _factory;

    public sealed record BrowserFeed(string Path, int Width, int Height, int Fps);

    public BrowserBridge(string scratchDir, Func<StudioSource, IFrameSource?> factory)
    {
        _scratch = scratchDir;
        _factory = factory;
    }

    public static (int W, int H) SizeOf(StudioSource s) =>
        (SmartTuner.Even(Math.Clamp(s.BrowserWidth, 64, 3840)), SmartTuner.Even(Math.Clamp(s.BrowserHeight, 64, 2160)));

    public static int FpsOf(StudioSource s) => Math.Clamp(s.BrowserFps, 1, 60);

    public Dictionary<string, BrowserFeed> Open(IReadOnlyDictionary<string, StudioSource> browsers, double origin, Action<string>? warn = null)
    {
        ClosePipes();
        foreach (var key in _sources.Keys.Except(browsers.Keys).ToList()) { _sources[key].Dispose(); _sources.Remove(key); }
        var result = new Dictionary<string, BrowserFeed>();
        foreach (var (key, src) in browsers)
        {
            if (!_sources.TryGetValue(key, out var fs))
            {
                IFrameSource? made = null;
                try { made = _factory(src); } catch (Exception ex) { warn?.Invoke($"\"{src.Name}\": {ex.Message}"); }
                if (made is null) { warn?.Invoke($"\"{src.Name}\": browser sources need the Microsoft Edge WebView2 runtime (Windows)."); continue; }
                fs = made;
                _sources[key] = fs;
            }
            if (fs.Error is { } err) warn?.Invoke($"\"{src.Name}\": {err}");
            var (w, h) = (fs.Width, fs.Height);
            int fps = FpsOf(src);
            var blank = new byte[w * h * 4]; // fully transparent until the page has drawn
            var source = fs;
            var pipe = new CountedFramePipe(_scratch, blank.Length, fps, origin, () => source.Latest(), blank);
            _pipes.Add(pipe);
            result[key] = new BrowserFeed(pipe.Path, w, h, fps);
        }
        return result;
    }

    public void ClosePipes()
    {
        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
    }

    public void CloseAll()
    {
        ClosePipes();
        foreach (var s in _sources.Values) { try { s.Dispose(); } catch { } }
        _sources.Clear();
    }

    public void Dispose() => CloseAll();
}
