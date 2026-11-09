using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LeapcastStudioAI;

/// <summary>
/// A web page (chat, alerts, widgets) rendered off-screen by Microsoft Edge WebView2. Frames come from Chromium's
/// screencast, which only sends a picture when the page actually changes, so an idle chat costs almost nothing.
/// The page background is transparent, and the source's CSS is injected into every page it loads.
/// </summary>
public sealed class WebViewFrameSource : IFrameSource
{
    private static Task<CoreWebView2Environment>? _environment;
    private readonly Dispatcher _ui;
    private readonly Window _host;
    private readonly WebView2 _view;
    private readonly object _lock = new();
    private byte[]? _latest;
    private volatile bool _decoding, _disposed;
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private readonly DispatcherTimer _fallback;
    private readonly double _minInterval;

    /// <summary>The picture's size: the page's own size, or smaller when it's drawn smaller (<see cref="Resize"/>).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The page's layout size (CSS pixels): what the page is built for, whatever size it's drawn at.</summary>
    private readonly int _pageW, _pageH;

    private WebViewFrameSource(StudioSource s, Dispatcher ui)
    {
        _ui = ui;
        (Width, Height) = BrowserBridge.SizeOf(s);
        (_pageW, _pageH) = (Width, Height);
        _minInterval = 1.0 / BrowserBridge.FpsOf(s);
        _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        _host = new Window
        {
            Title = "Leapcast browser source", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            Left = -32000, Top = -32000, Width = Width, Height = Height, Content = _view,
        };
        _host.Show();
        // If the screencast stays silent (old runtimes), fall back to periodic snapshots of the page.
        _fallback = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(_minInterval) };
        _fallback.Tick += async (_, _) => await SnapshotAsync();
        _ = InitAsync(s);
    }

    /// <summary>Creates the source on the UI thread (WebView2 is a UI control).</summary>
    public static WebViewFrameSource Create(StudioSource s, Dispatcher ui) =>
        ui.CheckAccess() ? new WebViewFrameSource(s, ui) : ui.Invoke(() => new WebViewFrameSource(s, ui));

    private static Task<CoreWebView2Environment> SharedEnvironment()
    {
        if (_environment is null)
            // the status bar's CPU/RAM breakdown counts the browser engine's processes (asked on the UI thread)
            ResourceMonitor.BrowserProcessIds = () => _environment is { IsCompletedSuccessfully: true } t
                ? t.Result.GetProcessInfos().Select(p => p.ProcessId).ToList()
                : Array.Empty<int>();
        return _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(StateStore.Folder, "webview2"),
            new CoreWebView2EnvironmentOptions(
                // keep rendering while the window is off-screen/unfocused (it's never "visible" to Chromium)
                "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling " +
                "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required"));
    }

    private async Task InitAsync(StudioSource s)
    {
        try
        {
            var env = await SharedEnvironment();
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            string css = JsonSerializer.Serialize(s.BrowserCss ?? "");
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "(() => { const add = () => { const st = document.createElement('style'); st.textContent = " + css + "; " +
                "(document.head || document.documentElement).appendChild(st); }; " +
                "if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add); else add(); })();");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride", "{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", MetricsJson());
            var frames = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
            frames.DevToolsProtocolEventReceived += OnScreencastFrame;
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) Error = $"the page couldn't be loaded ({e.WebErrorStatus}).";
                else Error = null;
            };
            core.Navigate(NormalizeUrl(s.Url));
            await core.CallDevToolsProtocolMethodAsync("Page.startScreencast", ScreencastJson());
            _ready = true;
            _ = _ui.InvokeAsync(async () =>
            {
                await Task.Delay(4000);
                if (!_disposed && _lastFrameUtc == DateTime.MinValue) _fallback.Start();
            });
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Error = "the Microsoft Edge WebView2 Runtime isn't installed (get it from Microsoft's website).";
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private volatile bool _ready;

    /// <summary>The page keeps its layout size; Chromium draws it at the picture's size (fewer pixels to draw,
    /// encode, decode and pipe when the page is shown smaller than it's built, e.g. a 1080p alert box on a 540p stream).</summary>
    private string MetricsJson() => FormattableString.Invariant(
        $"{{\"width\":{_pageW},\"height\":{_pageH},\"deviceScaleFactor\":{Math.Min(1.0, Width / (double)_pageW):0.####},\"mobile\":false}}");

    private string ScreencastJson() => $"{{\"format\":\"png\",\"maxWidth\":{Width},\"maxHeight\":{Height},\"everyNthFrame\":1}}";

    public void Resize(int w, int h)
    {
        if (_disposed || (w == Width && h == Height)) return;
        lock (_lock) { Width = w; Height = h; _latest = null; }
        _ui.BeginInvoke(async () =>
        {
            try
            {
                var core = _view.CoreWebView2;
                if (core is null || _disposed || !_ready) return;   // InitAsync applies the new size itself
                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", MetricsJson());
                await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
                await core.CallDevToolsProtocolMethodAsync("Page.startScreencast", ScreencastJson());
            }
            catch { }
        });
    }

    public void Refresh(bool clearCache)
    {
        if (_disposed) return;
        _ui.BeginInvoke(async () =>
        {
            try
            {
                var core = _view.CoreWebView2;
                if (core is null || _disposed) return;
                // the cache is shared by every page; sign-ins (cookies) are kept
                if (clearCache) await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
                await core.CallDevToolsProtocolMethodAsync("Page.reload", clearCache ? "{\"ignoreCache\":true}" : "{}");
            }
            catch (Exception ex) { Error = ex.Message; }
        });
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "about:blank";
        if (File.Exists(url)) return new Uri(Path.GetFullPath(url)).AbsoluteUri;
        return url.Contains("://", StringComparison.Ordinal) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    private async void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            int session = doc.RootElement.GetProperty("sessionId").GetInt32();
            string data = doc.RootElement.GetProperty("data").GetString() ?? "";
            // acknowledge right away so Chromium keeps sending frames
            _ = _view.CoreWebView2?.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{session}}}");
            _fallback.Stop();
            Accept(Convert.FromBase64String(data));
        }
        catch { }
        await Task.CompletedTask;
    }

    private async Task SnapshotAsync()
    {
        if (_disposed || _view.CoreWebView2 is null || _decoding) return;
        try
        {
            using var ms = new MemoryStream();
            await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            Accept(ms.ToArray());
        }
        catch { }
    }

    /// <summary>Decodes a PNG off the UI thread (at most the source's frame rate) into the latest BGRA frame.</summary>
    private void Accept(byte[] png)
    {
        var now = DateTime.UtcNow;
        if (_decoding || (now - _lastFrameUtc).TotalSeconds < _minInterval * 0.8) return;
        _decoding = true;
        _lastFrameUtc = now;
        Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(png);
                var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                int W = Width, H = Height;
                // a full-size picture (the fallback snapshot, or a frame from before a resize): scaled to the picture size
                if (frame.PixelWidth > W * 1.05 && frame.PixelWidth >= _pageW * 0.95)
                    frame = new TransformedBitmap(frame, new ScaleTransform(W / (double)frame.PixelWidth, H / (double)frame.PixelHeight));
                var buf = new byte[W * H * 4];
                int w = Math.Min(W, frame.PixelWidth), h = Math.Min(H, frame.PixelHeight);
                if (w == W && h == H) frame.CopyPixels(buf, W * 4, 0);
                else
                {
                    var part = new byte[w * h * 4];
                    frame.CopyPixels(new Int32Rect(0, 0, w, h), part, w * 4, 0);
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(part, y * w * 4, buf, y * W * 4, w * 4);
                }
                lock (_lock) { if (W == Width && H == Height) _latest = buf; }
            }
            catch { }
            finally { _decoding = false; }
        });
    }

    public byte[]? Latest()
    {
        lock (_lock) return _latest;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // never block: the UI thread may itself be waiting for the engine to stop
        _ui.BeginInvoke(() =>
        {
            try { _fallback.Stop(); } catch { }
            try { _view.Dispose(); } catch { }
            try { _host.Close(); } catch { }
        });
    }
}
