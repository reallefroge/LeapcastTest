using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LeapcastStudioAI;

/// <summary>
/// <see cref="IGpuCanvas"/> on Direct3D 11. One thread draws the canvas at the frame rate: sources are uploaded
/// only when they have a new picture (YUV pictures as three one-byte planes, converted to colour while drawing;
/// pages and images as BGRA), each layer is one textured quad (scale and crop are just where it's drawn and which
/// part of the texture it samples), a fade mixes two scenes, and the result is turned back into YUV 4:2:0 on the
/// GPU so only 1.5 bytes a pixel come back.
/// </summary>
public sealed unsafe class D3DCanvas : IGpuCanvas
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float X0, Y0, X1, Y1;          // where (−1…1)
        public float U0, V0, U1, V1;          // which part of the texture
        public float Gamma, Contrast, Saturation, Brightness;
        public float Opacity, Mix, Pad0, Pad1;
    }

    private const string Hlsl = @"
cbuffer P : register(b0) { float4 dst; float4 uv; float4 eqv; float4 misc; };
struct VO { float4 pos : SV_Position; float2 tc : TEXCOORD0; };
VO VS(uint id : SV_VertexID)
{
    float2 c = float2(id & 1, id >> 1);
    VO o;
    o.pos = float4(lerp(dst.x, dst.z, c.x), lerp(dst.y, dst.w, c.y), 0, 1);
    o.tc = float2(lerp(uv.x, uv.z, c.x), lerp(uv.y, uv.w, c.y));
    return o;
}
Texture2D t0 : register(t0);
Texture2D t1 : register(t1);
Texture2D t2 : register(t2);
SamplerState s0 : register(s0);
float3 Eq(float3 c)
{
    c = pow(saturate(c), 1.0 / eqv.x);
    c = (c - 0.5) * eqv.y + 0.5 + eqv.w;
    float l = dot(c, float3(0.299, 0.587, 0.114));
    return saturate(lerp(float3(l, l, l), c, eqv.z));
}
float4 PS_I420(VO i) : SV_Target
{
    float y = (t0.Sample(s0, i.tc).r - 16.0 / 255.0) * (255.0 / 219.0);
    float u = (t1.Sample(s0, i.tc).r - 128.0 / 255.0) * (255.0 / 224.0);
    float v = (t2.Sample(s0, i.tc).r - 128.0 / 255.0) * (255.0 / 224.0);
    float3 c = float3(y + 1.402 * v, y - 0.344136 * u - 0.714136 * v, y + 1.772 * u);
    return float4(Eq(c), misc.x);
}
float4 PS_BGRA(VO i) : SV_Target
{
    float4 c = t0.Sample(s0, i.tc);
    return float4(Eq(c.rgb), c.a * misc.x);
}
float4 PS_MIX(VO i) : SV_Target
{
    return lerp(t0.Sample(s0, i.tc), t1.Sample(s0, i.tc), misc.y);
}
float PS_Y(VO i) : SV_Target
{
    float3 c = t0.Sample(s0, i.tc).rgb;
    return (16.0 + 65.481 * c.r + 128.553 * c.g + 24.966 * c.b) / 255.0;
}
struct UVO { float u : SV_Target0; float v : SV_Target1; };
UVO PS_UV(VO i)
{
    float3 c = t0.Sample(s0, i.tc).rgb;
    UVO o;
    o.u = (128.0 - 37.797 * c.r - 74.203 * c.g + 112.0 * c.b) / 255.0;
    o.v = (128.0 + 112.0 * c.r - 93.786 * c.g - 18.214 * c.b) / 255.0;
    return o;
}";

    private sealed class Source
    {
        public byte[]? Uploaded;
        public int W, H;
        public FramePixels Format;
        public ID3D11Texture2D?[] Tex = new ID3D11Texture2D?[3];
        public ID3D11ShaderResourceView?[] Srv = new ID3D11ShaderResourceView?[3];
        public void Release() { for (int i = 0; i < 3; i++) { Srv[i]?.Dispose(); Tex[i]?.Dispose(); Srv[i] = null; Tex[i] = null; } }
    }

    private sealed class Target
    {
        public required ID3D11Texture2D Tex;
        public required ID3D11RenderTargetView Rtv;
        public ID3D11ShaderResourceView? Srv;
        public void Dispose() { Srv?.Dispose(); Rtv.Dispose(); Tex.Dispose(); }
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psI420, _psBgra, _psMix, _psY, _psUv;
    private readonly ID3D11Buffer _params;
    private readonly ID3D11SamplerState _linear;
    private readonly ID3D11BlendState _blend, _opaque;
    private readonly ID3D11RasterizerState _raster;
    private readonly Target _canvas, _sceneA, _sceneB, _y, _u, _v;
    private readonly ID3D11Texture2D _stageY, _stageU, _stageV;
    private readonly Func<string, GpuFrame?> _frames;
    private readonly Dictionary<string, Source> _sources = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private GpuState _state = GpuState.Empty;
    private readonly object _stateLock = new();
    private byte[]? _latest;
    private readonly object _latestLock = new();
    private long _frameCount, _uploads;
    private double _frameMs;
    private static readonly Color4 Background = new(0x0E / 255f, 0x10 / 255f, 0x16 / 255f, 1f);

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Adapter { get; } = "";
    public long Frames => Interlocked.Read(ref _frameCount);
    public long Uploads => Interlocked.Read(ref _uploads);
    public double FrameMs => Volatile.Read(ref _frameMs);
    public string? Error { get; private set; }

    /// <param name="software">Use Windows' software renderer (WARP) instead of the graphics card (tests, or no usable GPU).</param>
    public D3DCanvas(int width, int height, int fps, Func<string, GpuFrame?> frames, bool software = false)
    {
        Width = width & ~1; Height = height & ~1; Fps = Math.Clamp(fps, 1, 120);
        _frames = frames;
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        ID3D11Device? device = null; ID3D11DeviceContext? ctx = null;
        bool ok = !software && D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device, out ctx).Success;
        if (!ok || device is null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out device, out ctx).CheckError();
            software = true;
        }
        _device = device!; _ctx = ctx!;
        try
        {
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            Adapter = adapter.Description.Description + (software ? " (software)" : "");
        }
        catch { Adapter = software ? "Microsoft Basic Render Driver (software)" : "GPU"; }

        ReadOnlyMemory<byte> Compile(string entry, string profile) => Compiler.Compile(Hlsl, entry, "leapcast-canvas.hlsl", profile, ShaderFlags.OptimizationLevel3);
        _vs = _device.CreateVertexShader(Compile("VS", "vs_4_0").Span);
        _psI420 = _device.CreatePixelShader(Compile("PS_I420", "ps_4_0").Span);
        _psBgra = _device.CreatePixelShader(Compile("PS_BGRA", "ps_4_0").Span);
        _psMix = _device.CreatePixelShader(Compile("PS_MIX", "ps_4_0").Span);
        _psY = _device.CreatePixelShader(Compile("PS_Y", "ps_4_0").Span);
        _psUv = _device.CreatePixelShader(Compile("PS_UV", "ps_4_0").Span);
        _params = _device.CreateBuffer(new BufferDescription((uint)sizeof(Params), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _linear = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _blend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _opaque = _device.CreateBlendState(BlendDescription.Opaque);
        _raster = _device.CreateRasterizerState(RasterizerDescription.CullNone);

        _canvas = MakeTarget(Width, Height, Format.B8G8R8A8_UNorm, true);
        _sceneA = MakeTarget(Width, Height, Format.B8G8R8A8_UNorm, true);
        _sceneB = MakeTarget(Width, Height, Format.B8G8R8A8_UNorm, true);
        _y = MakeTarget(Width, Height, Format.R8_UNorm, false);
        _u = MakeTarget(Width / 2, Height / 2, Format.R8_UNorm, false);
        _v = MakeTarget(Width / 2, Height / 2, Format.R8_UNorm, false);
        _stageY = Staging(Width, Height);
        _stageU = Staging(Width / 2, Height / 2);
        _stageV = Staging(Width / 2, Height / 2);

        _thread = new Thread(Run) { IsBackground = true, Name = "Leapcast GPU canvas", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private Target MakeTarget(int w, int h, Format f, bool readable)
    {
        var tex = _device.CreateTexture2D(new Texture2DDescription(f, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        return new Target { Tex = tex, Rtv = _device.CreateRenderTargetView(tex), Srv = readable ? _device.CreateShaderResourceView(tex) : null };
    }

    private ID3D11Texture2D Staging(int w, int h) =>
        _device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

    public void SetState(GpuState state) { lock (_stateLock) _state = state; }

    public byte[]? Latest() { lock (_latestLock) return _latest; }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double period = 1.0 / Fps, avg = 0;
        long n = 0;
        while (!_stop)
        {
            try
            {
                double t0 = clock.Elapsed.TotalSeconds;
                GpuState st;
                lock (_stateLock) st = _state;
                Render(st);
                var frame = ReadBack();
                lock (_latestLock) _latest = frame;
                double ms = (clock.Elapsed.TotalSeconds - t0) * 1000;
                avg = n == 0 ? ms : avg * 0.95 + ms * 0.05;
                Volatile.Write(ref _frameMs, avg);
                Interlocked.Increment(ref _frameCount);
                n++;
                // next frame on the frame-rate grid (no drift)
                double next = n * period, wait = next - clock.Elapsed.TotalSeconds;
                if (wait > 0.002) Thread.Sleep(TimeSpan.FromSeconds(wait - 0.001));
                while (clock.Elapsed.TotalSeconds < next) Thread.SpinWait(50);
                if (clock.Elapsed.TotalSeconds - next > 0.5) n = (long)(clock.Elapsed.TotalSeconds / period); // fell far behind: skip ahead
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Thread.Sleep(200);
            }
        }
    }

    private void Render(GpuState st)
    {
        _ctx.RSSetState(_raster);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _ctx.VSSetShader(_vs);
        _ctx.VSSetConstantBuffer(0, _params);
        _ctx.PSSetConstantBuffer(0, _params);
        _ctx.PSSetSampler(0, _linear);

        double mix = st.Mix(DateTime.UtcNow);
        bool fading = st.From is int from && mix < 1 && from >= 0 && from < st.Scenes.Count;
        if (!fading)
        {
            DrawScene(_canvas, st.Active >= 0 && st.Active < st.Scenes.Count ? st.Scenes[st.Active] : null);
        }
        else
        {
            DrawScene(_sceneA, st.Scenes[st.From!.Value]);
            DrawScene(_sceneB, st.Active < st.Scenes.Count ? st.Scenes[st.Active] : null);
            _ctx.OMSetRenderTargets(_canvas.Rtv);
            _ctx.RSSetViewport(0, 0, Width, Height);
            _ctx.OMSetBlendState(_opaque);
            SetParams(new Params { X0 = -1, Y0 = 1, X1 = 1, Y1 = -1, U1 = 1, V1 = 1, Gamma = 1, Contrast = 1, Saturation = 1, Opacity = 1, Mix = (float)mix });
            _ctx.PSSetShader(_psMix);
            _ctx.PSSetShaderResources(0, new[] { _sceneA.Srv!, _sceneB.Srv! });
            _ctx.Draw(4, 0);
            _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
        }

        // canvas → Y (full size) and U, V (half size): averaged 2×2 by the sampler
        _ctx.OMSetBlendState(_opaque);
        var full = new Params { X0 = -1, Y0 = 1, X1 = 1, Y1 = -1, U1 = 1, V1 = 1, Gamma = 1, Contrast = 1, Saturation = 1, Opacity = 1 };
        SetParams(full);
        _ctx.PSSetShaderResource(0, _canvas.Srv!);
        _ctx.OMSetRenderTargets(_y.Rtv);
        _ctx.RSSetViewport(0, 0, Width, Height);
        _ctx.PSSetShader(_psY);
        _ctx.Draw(4, 0);
        _ctx.OMSetRenderTargets(new[] { _u.Rtv, _v.Rtv });
        _ctx.RSSetViewport(0, 0, Width / 2, Height / 2);
        _ctx.PSSetShader(_psUv);
        _ctx.Draw(4, 0);
        _ctx.OMSetRenderTargets(new ID3D11RenderTargetView[] { null!, null! });
        _ctx.PSSetShaderResource(0, null!);
        _ctx.CopyResource(_stageY, _y.Tex);
        _ctx.CopyResource(_stageU, _u.Tex);
        _ctx.CopyResource(_stageV, _v.Tex);
    }

    private void DrawScene(Target target, GpuScene? scene)
    {
        _ctx.OMSetRenderTargets(target.Rtv);
        _ctx.RSSetViewport(0, 0, Width, Height);
        _ctx.ClearRenderTargetView(target.Rtv, Background);
        if (scene is null) return;
        _ctx.OMSetBlendState(_blend);
        foreach (var layer in scene.Layers)
        {
            if (layer.Opacity <= 0 || layer.Rect.W < 1 || layer.Rect.H < 1) continue;
            var src = Upload(layer.Key);
            if (src is null) continue;
            var (x0, y0, x1, y1) = GpuCanvasMath.Ndc(layer.Rect, Width, Height);
            var l = layer.Light ?? LightSettings.Neutral;
            float u0 = (float)layer.U0, u1 = (float)layer.U1;
            if (layer.Mirror) (u0, u1) = (u1, u0);
            SetParams(new Params
            {
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, U0 = u0, V0 = (float)layer.V0, U1 = u1, V1 = (float)layer.V1,
                Gamma = (float)l.Gamma, Contrast = (float)l.Contrast, Saturation = (float)l.Saturation, Brightness = (float)l.Brightness,
                Opacity = (float)Math.Clamp(layer.Opacity, 0, 1),
            });
            if (src.Format == FramePixels.I420)
            {
                _ctx.PSSetShader(_psI420);
                _ctx.PSSetShaderResources(0, new[] { src.Srv[0]!, src.Srv[1]!, src.Srv[2]! });
            }
            else
            {
                _ctx.PSSetShader(_psBgra);
                _ctx.PSSetShaderResource(0, src.Srv[0]!);
            }
            _ctx.Draw(4, 0);
        }
        _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null! });
    }

    private void SetParams(Params p) => _ctx.UpdateSubresource(new MappedSubresource((IntPtr)(&p), (uint)sizeof(Params), 0), _params, 0);

    /// <summary>The source's texture(s), uploaded again only when it has a new picture.</summary>
    private Source? Upload(string key)
    {
        GpuFrame? f;
        try { f = _frames(key); } catch { f = null; }
        _sources.TryGetValue(key, out var s);
        if (f is null) return s is { Uploaded: not null } ? s : null;
        if (s is not null && ReferenceEquals(s.Uploaded, f.Data)) return s;
        int w = f.Width & ~1, h = f.Height & ~1;
        if (w < 2 || h < 2) return s;
        int need = f.Format == FramePixels.I420 ? f.Width * f.Height * 3 / 2 : f.Width * f.Height * 4;
        if (f.Data.Length < need) return s;
        if (s is null) { s = new Source(); _sources[key] = s; }
        if (s.W != f.Width || s.H != f.Height || s.Format != f.Format || s.Tex[0] is null)
        {
            s.Release();
            s.W = f.Width; s.H = f.Height; s.Format = f.Format;
            if (f.Format == FramePixels.I420)
            {
                s.Tex[0] = Plane(f.Width, f.Height, Format.R8_UNorm);
                s.Tex[1] = Plane(f.Width / 2, f.Height / 2, Format.R8_UNorm);
                s.Tex[2] = Plane(f.Width / 2, f.Height / 2, Format.R8_UNorm);
            }
            else s.Tex[0] = Plane(f.Width, f.Height, Format.B8G8R8A8_UNorm);
            for (int i = 0; i < 3; i++) if (s.Tex[i] is { } t) s.Srv[i] = _device.CreateShaderResourceView(t);
        }
        fixed (byte* p = f.Data)
        {
            if (f.Format == FramePixels.I420)
            {
                int cw = f.Width / 2, ch = f.Height / 2;
                _ctx.UpdateSubresource(new MappedSubresource((IntPtr)p, (uint)f.Width, 0), s.Tex[0]!, 0);
                _ctx.UpdateSubresource(new MappedSubresource((IntPtr)(p + f.Width * f.Height), (uint)cw, 0), s.Tex[1]!, 0);
                _ctx.UpdateSubresource(new MappedSubresource((IntPtr)(p + f.Width * f.Height + cw * ch), (uint)cw, 0), s.Tex[2]!, 0);
            }
            else _ctx.UpdateSubresource(new MappedSubresource((IntPtr)p, (uint)(f.Width * 4), 0), s.Tex[0]!, 0);
        }
        s.Uploaded = f.Data;
        Interlocked.Increment(ref _uploads);
        return s;
    }

    private ID3D11Texture2D Plane(int w, int h, Format f) =>
        _device.CreateTexture2D(new Texture2DDescription(f, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));

    /// <summary>The finished canvas as YUV 4:2:0 (rows copied out of the GPU's padded layout).</summary>
    private byte[] ReadBack()
    {
        int w = Width, h = Height, cw = w / 2, ch = h / 2;
        var o = new byte[w * h * 3 / 2];
        void Copy(ID3D11Texture2D stage, int pw, int ph, int offset)
        {
            var m = _ctx.Map(stage, 0, MapMode.Read);
            try
            {
                byte* src = (byte*)m.DataPointer;
                fixed (byte* dst = o)
                    for (int y = 0; y < ph; y++) Buffer.MemoryCopy(src + (long)y * m.RowPitch, dst + offset + y * pw, pw, pw);
            }
            finally { _ctx.Unmap(stage, 0); }
        }
        Copy(_stageY, w, h, 0);
        Copy(_stageU, cw, ch, w * h);
        Copy(_stageV, cw, ch, w * h + cw * ch);
        return o;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        foreach (var s in _sources.Values) s.Release();
        _sources.Clear();
        foreach (var t in new[] { _canvas, _sceneA, _sceneB, _y, _u, _v }) t.Dispose();
        _stageY.Dispose(); _stageU.Dispose(); _stageV.Dispose();
        _vs.Dispose(); _psI420.Dispose(); _psBgra.Dispose(); _psMix.Dispose(); _psY.Dispose(); _psUv.Dispose();
        _params.Dispose(); _linear.Dispose(); _blend.Dispose(); _opaque.Dispose(); _raster.Dispose();
        _ctx.ClearState(); _ctx.Flush();
        _ctx.Dispose(); _device.Dispose();
    }
}
