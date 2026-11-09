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
    /// <summary>Draws the page at this size from now on (its layout stays the same): it's shown smaller than it's built.</summary>
    void Resize(int width, int height) { }
    /// <summary>Loads the page again (<paramref name="clearCache"/>: the browser's cache is emptied first, so
    /// every file comes fresh from the site). The last picture stays up until the page draws again.</summary>
    void Refresh(bool clearCache) { }
}

/// <summary>
/// Keeps each browser source rendering (pages stay loaded across pipeline restarts, so chat history isn't lost)
/// and feeds it to the pipeline through a <see cref="CountedFramePipe"/> at the source's own, low frame rate.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    private readonly Dictionary<string, IFrameSource> _sources = new();
    private volatile Dictionary<string, IFrameSource> _snapshot = new();   // read from other threads (GPU canvas)
    private readonly List<CountedFramePipe> _pipes = new();
    private readonly string _scratch;
    private readonly Func<StudioSource, IFrameSource?> _factory;

    public sealed record BrowserFeed(string Path, int Width, int Height, int Fps);

    /// <summary>What the pipe carries: YUV 4:2:0 with alpha (2.5 bytes a pixel instead of 4, and no conversion in FFmpeg).</summary>
    public const string PixelFormat = "yuva420p";

    public BrowserBridge(string scratchDir, Func<StudioSource, IFrameSource?> factory)
    {
        _scratch = scratchDir;
        _factory = factory;
    }

    public static (int W, int H) SizeOf(StudioSource s) =>
        (SmartTuner.Even(Math.Clamp(s.BrowserWidth, 64, 3840)), SmartTuner.Even(Math.Clamp(s.BrowserHeight, 64, 2160)));

    public static int FpsOf(StudioSource s) => Math.Clamp(s.BrowserFps, 1, 60);

    /// <param name="renderSize">The size each page is drawn at (null: its own size).</param>
    public Dictionary<string, BrowserFeed> Open(IReadOnlyDictionary<string, StudioSource> browsers, double origin, Action<string>? warn = null,
        Func<StudioSource, (int W, int H)>? renderSize = null)
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
            if (renderSize is not null) { var (rw, rh) = renderSize(src); fs.Resize(rw, rh); }
            var (w, h) = (fs.Width, fs.Height);
            int fps = FpsOf(src);
            var blank = Yuva.Blank(w, h); // fully transparent until the page has drawn
            var source = fs;
            var convert = new Yuva(w, h);
            // the page as the overlay blends it (YUV + alpha), converted only when the page changed
            var pipe = new CountedFramePipe(_scratch, blank.Length, fps, origin, () => convert.From(source.Latest()), blank);
            _pipes.Add(pipe);
            result[key] = new BrowserFeed(pipe.Path, w, h, fps);
        }
        _snapshot = new Dictionary<string, IFrameSource>(_sources);
        return result;
    }

    /// <summary>Reloads one page (Refresh / Clear cache). False when it isn't open.</summary>
    public bool Refresh(string key, bool clearCache)
    {
        if (!_snapshot.TryGetValue(key, out var fs)) return false;
        fs.Refresh(clearCache);
        return true;
    }

    /// <summary>A page's latest picture (BGRA), for the GPU canvas.</summary>
    public GpuFrame? LatestBgra(string key)
    {
        _snapshot.TryGetValue(key, out var fs);
        if (fs is null) return null;
        int w = fs.Width, h = fs.Height;
        return fs.Latest() is { } f && f.Length == w * h * 4 ? new GpuFrame(f, w, h, FramePixels.Bgra) : null;
    }

    /// <summary>Deepest frame backlog of any browser pipe (seconds).</summary>
    public double MaxBacklogSeconds() => _pipes.ToList().Select(p => p.BacklogSeconds).DefaultIfEmpty(0).Max();

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
        _snapshot = new();
    }

    public void Dispose() => CloseAll();
}

/// <summary>
/// BGRA (straight alpha) → YUVA 4:2:0 (BT.601, limited range: what FFmpeg's overlay assumes), cached: a page that
/// didn't change since the last picture costs nothing.
/// </summary>
public sealed class Yuva
{
    private readonly int _w, _h;
    private byte[]? _from, _out;
    public int Conversions { get; private set; }

    public Yuva(int w, int h) { _w = w; _h = h; }

    public static int Size(int w, int h) => w * h * 2 + (w / 2) * (h / 2) * 2;

    public static byte[] Blank(int w, int h)
    {
        var b = new byte[Size(w, h)];
        Array.Fill(b, (byte)16, 0, w * h);
        Array.Fill(b, (byte)128, w * h, (w / 2) * (h / 2) * 2);
        // alpha (last plane) stays 0: transparent
        return b;
    }

    public byte[]? From(byte[]? bgra)
    {
        if (bgra is null || bgra.Length != _w * _h * 4) return null;   // (a picture from before a resize: not this size)
        if (ReferenceEquals(bgra, _from) && _out is not null) return _out;
        var o = new byte[Size(_w, _h)];
        Convert(bgra, _w, _h, o);
        _from = bgra; _out = o; Conversions++;
        return o;
    }

    public static void Convert(byte[] bgra, int w, int h, byte[] o)
    {
        int cw = w / 2, ch = h / 2, uOff = w * h, vOff = uOff + cw * ch, aOff = vOff + cw * ch;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4, yo = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                o[yo + x] = (byte)((66 * r + 129 * g + 25 * b + 128 >> 8) + 16);
                o[aOff + yo + x] = bgra[i + 3];
            }
        }
        // chroma: each 2×2 block, weighted by alpha (fully transparent pixels mustn't tint the edges)
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                long sr = 0, sg = 0, sb = 0, sa = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = ((2 * y + dy) * w + 2 * x + dx) * 4;
                        int a = bgra[i + 3] + 1;
                        sb += bgra[i] * a; sg += bgra[i + 1] * a; sr += bgra[i + 2] * a; sa += a;
                    }
                int r = (int)(sr / sa), g = (int)(sg / sa), bb = (int)(sb / sa);
                o[uOff + y * cw + x] = (byte)((-38 * r - 74 * g + 112 * bb + 128 >> 8) + 128);
                o[vOff + y * cw + x] = (byte)((112 * r - 94 * g - 18 * bb + 128 >> 8) + 128);
            }
    }
}
