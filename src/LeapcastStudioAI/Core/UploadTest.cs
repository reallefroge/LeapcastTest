using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace LeapcastStudioAI;

/// <summary>
/// Measures upload speed in kbps without needing a stream key: several parallel HTTPS uploads of random
/// (incompressible) data to a speed-test endpoint for a few seconds. The first seconds are skipped because TCP
/// ramps up, so the result is the sustained rate, which is what a stream needs.
/// When a Twitch output with a key exists, <see cref="BandwidthTester"/> is better: it measures a real RTMP stream to
/// the actual ingest, in Twitch's invisible test mode.
/// </summary>
public static class UploadSpeedTest
{
    /// <summary>Cloudflare's public speed-test upload endpoint (accepts a POST body and discards it).</summary>
    public const string DefaultEndpoint = "https://speed.cloudflare.com/__up";

    public static async Task<BandwidthResult> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default,
        string endpoint = DefaultEndpoint, int seconds = 10, int streams = 4)
    {
        long sent = 0;
        var sw = Stopwatch.StartNew();
        var samples = new List<(double t, long bytes)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds)); // requests still running at the end are simply cut off
        // A small send buffer: what's counted as "written" then really is on its way (a multi-megabyte buffer would
        // swallow seconds of upload instantly and make a slow line look fast for the first seconds).
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (ctx, token) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true, SendBufferSize = 128 * 1024,
                };
                try { await socket.ConnectAsync(ctx.DnsEndPoint, token); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(seconds + 10),
        };
        var chunk = new byte[64 * 1024];
        Random.Shared.NextBytes(chunk);
        Exception? lastError = null;
        int succeeded = 0;

        async Task Worker()
        {
            while (sw.Elapsed.TotalSeconds < seconds && !stop.IsCancellationRequested)
            {
                try
                {
                    // 2 MB per request (fixed length): keeps every connection busy on fast and slow lines alike
                    using var content = new CountingContent(chunk, 32, n => Interlocked.Add(ref sent, n));
                    using var resp = await http.PostAsync(endpoint, content, stop.Token);
                    if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent) Interlocked.Increment(ref succeeded);
                    else lastError = new HttpRequestException($"the test server answered {(int)resp.StatusCode}");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) { lastError = ex; await Task.Delay(300, CancellationToken.None); if (Interlocked.Read(ref sent) == 0 && sw.Elapsed.TotalSeconds > 4) return; }
            }
        }

        var workers = Enumerable.Range(0, Math.Max(1, streams)).Select(_ => Task.Run(Worker)).ToList();
        while (workers.Any(w => !w.IsCompleted))
        {
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(500, CancellationToken.None));
            double t = sw.Elapsed.TotalSeconds;
            long b = Interlocked.Read(ref sent);
            samples.Add((t, b));
            var from = samples.FirstOrDefault(x => x.t >= Math.Min(2, t - 1));
            double rate = t - from.t > 0.5 ? (b - from.bytes) * 8 / 1000.0 / (t - from.t) : 0;
            progress?.Report(rate > 0 ? $"Measuring your upload… {rate:N0} kbps" : "Connecting to the speed test…");
            ct.ThrowIfCancellationRequested();
        }

        // sustained rate: from 2 s in (after TCP ramp-up) to the end of the test window
        var start = samples.FirstOrDefault(x => x.t >= 2);
        var end = samples.LastOrDefault(x => x.t <= seconds + 0.6);
        if (end.bytes == 0 || end.t - start.t < 2 || succeeded == 0 && end.bytes < 256 * 1024)
            throw new InvalidOperationException("Couldn't reach the speed test" + (lastError is null ? "." : $": {lastError.Message.TrimEnd('.')}.") +
                                                " Check your internet connection, or type your upload speed in kbps.");
        double kbps = (end.bytes - start.bytes) * 8 / 1000.0 / (end.t - start.t);
        return new BandwidthResult(kbps, 0, false, $"Your upload sustained about {kbps:N0} kbps.");
    }

    /// <summary>A request body of repeated random data that reports every piece handed to the network.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] _chunk;
        private readonly int _count;
        private readonly Action<int> _sent;

        public CountingContent(byte[] chunk, int count, Action<int> sent)
        {
            _chunk = chunk; _count = count; _sent = sent;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (int i = 0; i < _count; i++)
            {
                await stream.WriteAsync(_chunk);
                _sent(_chunk.Length);
            }
        }

        protected override bool TryComputeLength(out long length) { length = (long)_chunk.Length * _count; return true; }
    }
}
