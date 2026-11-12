using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using LeapcastStudioAI.Vod;

namespace LeapcastStudioAI;

/// <summary>
/// The camera's person segmentation: OpenCV Zoo's PP-HumanSeg model (Apache 2.0, 192×192 RGB in, background /
/// person probabilities out), run on the processor with ONNX Runtime. Used by the VOD editor to blur or remove the
/// background behind you in your camera.
/// </summary>
public sealed class OnnxSegmenter : CameraLook.ISegmenter, IDisposable
{
    public const string ModelFile = "human_segmentation_pphumanseg_2023mar.onnx";
    private readonly InferenceSession _session;
    private readonly string _input;
    public int Size => 192;

    private OnnxSegmenter(string model)
    {
        var o = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) };
        _session = new InferenceSession(model, o);
        _input = _session.InputMetadata.Keys.First();
    }

    /// <summary>The model next to the app (models\…), or null when it isn't there or can't run.</summary>
    public static OnnxSegmenter? TryLoad(string? model = null)
    {
        try
        {
            model ??= Path.Combine(AppContext.BaseDirectory, "models", ModelFile);
            return File.Exists(model) ? new OnnxSegmenter(model) : null;
        }
        catch { return null; }
    }

    public float[] Run(byte[] rgb)
    {
        int n = Size, plane = n * n;
        var t = new DenseTensor<float>(new[] { 1, 3, n, n });
        var buf = t.Buffer.Span;
        for (int i = 0; i < plane; i++)
        {
            // (x / 255 − 0.5) / 0.5
            buf[i] = rgb[i * 3] / 127.5f - 1f;
            buf[plane + i] = rgb[i * 3 + 1] / 127.5f - 1f;
            buf[2 * plane + i] = rgb[i * 3 + 2] / 127.5f - 1f;
        }
        using var res = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, t) });
        var o = res.First().AsTensor<float>();
        var p = new float[plane];
        // channel 1: you
        if (o is DenseTensor<float> d) d.Buffer.Span.Slice(plane, plane).CopyTo(p);
        else for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) p[y * n + x] = o[0, 1, y, x];
        return p;
    }

    public void Dispose() => _session.Dispose();
}
