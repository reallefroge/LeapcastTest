namespace LeapcastStudioAI;

/// <summary>Reads timestamps straight from MPEG-TS packets (no FFmpeg process): where a segment starts.</summary>
public static class TsTimestamps
{
    /// <summary>
    /// The earliest video PTS (seconds) among the first PES packets of the file, or the earliest audio PTS when there's
    /// no video. Null when the file has none in its first 256 KB.
    /// </summary>
    public static double? FirstPts(string path)
    {
        byte[] buf;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            buf = new byte[Math.Min(f.Length, 256 * 1024)];
            int got = 0, n;
            while (got < buf.Length && (n = f.Read(buf, got, buf.Length - got)) > 0) got += n;
            if (got < buf.Length) Array.Resize(ref buf, got);
        }
        catch { return null; }
        return FirstPts(buf);
    }

    public static double? FirstPts(ReadOnlySpan<byte> ts)
    {
        long? video = null, audio = null;
        int videoSeen = 0;
        int start = ts.IndexOf((byte)0x47);
        if (start < 0) return null;
        for (int p = start; p + 188 <= ts.Length && videoSeen < 8; p += 188)
        {
            var pkt = ts.Slice(p, 188);
            if (pkt[0] != 0x47) { int next = ts[(p + 1)..].IndexOf((byte)0x47); if (next < 0) break; p += 1 + next - 188; continue; }
            bool unitStart = (pkt[1] & 0x40) != 0;
            int afc = (pkt[3] >> 4) & 3;
            if (!unitStart || (afc & 1) == 0) continue; // no payload, or not the start of a PES packet
            int off = 4;
            if ((afc & 2) != 0) off += 1 + pkt[4];
            if (off + 14 > 188) continue;
            if (pkt[off] != 0 || pkt[off + 1] != 0 || pkt[off + 2] != 1) continue;
            byte streamId = pkt[off + 3];
            bool isVideo = streamId is >= 0xE0 and <= 0xEF, isAudio = streamId is >= 0xC0 and <= 0xDF || streamId == 0xBD;
            if (!isVideo && !isAudio) continue;
            if ((pkt[off + 7] & 0x80) == 0) continue; // no PTS
            int h = off + 9;
            long pts = ((long)(pkt[h] & 0x0E) << 29) | ((long)pkt[h + 1] << 22) | ((long)(pkt[h + 2] & 0xFE) << 14) | ((long)pkt[h + 3] << 7) | ((long)pkt[h + 4] >> 1);
            if (isVideo) { video = video is null ? pts : Math.Min(video.Value, pts); videoSeen++; }
            else audio = audio is null ? pts : Math.Min(audio.Value, pts);
        }
        var best = video ?? audio;
        return best is null ? null : best.Value / 90000.0;
    }
}
