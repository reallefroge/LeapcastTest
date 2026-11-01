using System.IO.Pipes;

namespace LeapcastStudioAI;

/// <summary>
/// Windows named pipes between Leapcast and its ffmpeg processes (audio, cameras, game windows, the TS bus).
/// <para>
/// They are opened <b>synchronously</b> on purpose. Each pipe already has its own dedicated, above-normal-priority
/// thread, so a blocking read or write costs nothing. An overlapped (asynchronous) pipe would finish every write on
/// a .NET I/O completion thread at normal priority instead. When a game keeps every core busy, Windows only
/// schedules that thread at the end of the game's time slices, up to ~100 ms per write. Audio written 100 times a
/// second then arrives at a fraction of real time, and the whole stream crawls (measured: 0.07× on a busy 2-core PC).
/// </para>
/// </summary>
public static class LocalPipe
{
    public static NamedPipeServerStream Server(string name, PipeDirection direction, int inBuffer, int outBuffer) =>
        new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.None, inBuffer, outBuffer);

    /// <summary>Blocks until ffmpeg opens the pipe, or <paramref name="ct"/> is cancelled (then throws).</summary>
    public static void WaitForClient(NamedPipeServerStream server, string name, CancellationToken ct)
    {
        // A synchronous wait can't take a token: on cancel, connect a throw-away client so the wait returns.
        using var reg = ct.Register(() =>
        {
            try
            {
                var dir = server.CanWrite ? PipeDirection.In : PipeDirection.Out;
                using var c = new NamedPipeClientStream(".", name, dir);
                c.Connect(500);
            }
            catch { /* already connected or closed */ }
        });
        server.WaitForConnection();
        ct.ThrowIfCancellationRequested();
    }
}
