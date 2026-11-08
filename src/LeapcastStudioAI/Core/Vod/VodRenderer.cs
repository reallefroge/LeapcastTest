using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LeapcastStudioAI.Vod;

/// <summary>Turns an <see cref="EditPlan"/> into the finished video (plus a thumbnail and a description with chapters).</summary>
public static class VodRenderer
{
    private static string I(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public sealed record Result(string Video, string Thumbnail, string Description, TimeSpan Length);

    /// <param name="videoArgs">The final encode's video settings (the stream's encoder); intermediates use x264.</param>
    public static async Task<Result> RenderAsync(string ffmpeg, string source, EditPlan plan, string outputFolder, string baseName,
        IReadOnlyList<string> videoArgs, VodSignals signals, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outputFolder);
        string work = Path.Combine(outputFolder, ".work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // ---- every piece with its effects, exactly its number of frames
            var list = new StringBuilder();
            for (int i = 0; i < plan.Pieces.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = plan.Pieces[i];
                string file = Path.Combine(work, $"p{i:0000}.mkv");
                await RunAsync(ffmpeg, PieceArgs(source, p, plan, file), ct);
                list.AppendLine($"file '{file.Replace("\\", "/").Replace("'", "'\\''")}'");
                progress?.Report((0.05 + 0.75 * (i + 1) / plan.Pieces.Count, $"Editing: piece {i + 1} of {plan.Pieces.Count}"));
            }
            string listFile = Path.Combine(work, "pieces.txt");
            await File.WriteAllTextAsync(listFile, list.ToString(), ct);
            string body = Path.Combine(work, "body.mkv");
            await RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", body }, ct);

            // ---- music + sound effects, ducked under the stream's own sound
            progress?.Report((0.82, "Scoring the music"));
            string bed = Path.Combine(work, "bed.wav");
            await Task.Run(() => Dsp.WriteWav(bed, Bed(plan, signals)), ct);

            // ---- text, grade, mix, final encode
            progress?.Report((0.86, "Final render"));
            string ass = Path.Combine(work, "text.ass");
            await File.WriteAllTextAsync(ass, AssWriter.Build(plan), new UTF8Encoding(false), ct);
            string output = Unique(Path.Combine(outputFolder, baseName + ".mp4"));
            const string mix = "[0:a]highpass=f=70,acompressor=threshold=0.12:ratio=3:attack=8:release=180:makeup=1.6[p];" +
                               "[1:a]equalizer=f=2600:t=q:w=1.3:g=-3[b];[p][b]amix=inputs=2:normalize=0:duration=first";
            // loudness in two passes (measure, then apply exactly): YouTube plays everything at -14 LUFS
            string norm = "loudnorm=I=-14:TP=-1.5:LRA=11";
            try
            {
                var (_, report) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", body, "-i", bed, "-filter_complex",
                    mix + ",loudnorm=I=-14:TP=-1.5:LRA=11:print_format=json[a]", "-map", "[a]", "-f", "null", "-" }, TimeSpan.FromMinutes(30), ct);
                string M(string key) => System.Text.RegularExpressions.Regex.Match(report, $"\"{key}\"\\s*:\\s*\"(-?[0-9.inf]+)\"").Groups[1].Value;
                if (M("input_i") is { Length: > 0 } ii && !ii.Contains("inf") && M("input_tp") is { Length: > 0 } tp && M("input_lra") is { Length: > 0 } lra
                    && M("input_thresh") is { Length: > 0 } th && M("target_offset") is { Length: > 0 } off)
                    norm = $"loudnorm=I=-14:TP=-1.5:LRA=11:measured_I={ii}:measured_TP={tp}:measured_LRA={lra}:measured_thresh={th}:offset={off}:linear=true";
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            var args = new List<string> { "-y", "-v", "error", "-i", body, "-i", bed, "-filter_complex",
                $"[0:v]eq=contrast=1.05:saturation=1.12:gamma=0.98,vignette=angle=PI/5,ass='{VerticalClip.EscapeFilter(ass)}',format=yuv420p[v];" +
                mix + $",{norm},aresample=48000[a]",
                "-map", "[v]", "-map", "[a]" };
            args.AddRange(videoArgs);
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output });
            await RunAsync(ffmpeg, args, ct);

            // ---- thumbnail and description
            progress?.Report((0.97, "Thumbnail and chapters"));
            string thumb = Path.ChangeExtension(output, ".jpg");
            await Thumbnail(ffmpeg, source, plan, thumb, ct);
            string desc = Path.ChangeExtension(output, ".txt");
            await File.WriteAllTextAsync(desc, Description(plan), ct);
            progress?.Report((1, "Done"));
            return new Result(output, thumb, desc, TimeSpan.FromSeconds(plan.Duration));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            if (!File.Exists(p)) return p;
        }
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>The ffmpeg command for one piece: trim, slow motion, face crop, punch-ins, whips, shake, letterbox, blur.</summary>
    public static List<string> PieceArgs(string source, Piece p, EditPlan plan, string output)
    {
        int W = plan.Width, H = plan.Height, frames = plan.FramesOf(p);
        double F = plan.Fps, len = frames / F;
        var args = new List<string> { "-y", "-v", "error" };
        if (p.Hold > 0) args.AddRange(new[] { "-ss", I(Math.Max(0, p.In)), "-t", "0.2" });
        else args.AddRange(new[] { "-ss", I(p.In), "-t", I(p.Out - p.In + 0.1) });
        args.AddRange(new[] { "-i", source });
        var v = new List<string>();
        v.Add(p.Speed < 1 ? $"setpts=(PTS-STARTPTS)/{I(p.Speed)}" : "setpts=PTS-STARTPTS");
        if (p.Hold > 0) v.Add($"tpad=stop_mode=clone:stop_duration={I(p.Hold + 1)}");
        if (p.Speed < 1) v.Add($"minterpolate=fps={I(F)}:mi_mode=blend");
        v.Add($"fps={I(F)}");
        if (p.Crop is { } cam && plan.Layout is { CanvasW: > 0 } lay)
        {
            // the face: the camera's spot on the picture, widened to the video's shape
            double sx = (double)W / lay.CanvasW, sy = (double)H / lay.CanvasH;
            double cw = cam.W * sx, ch = cam.H * sy, cx = (cam.X + cam.W / 2) * sx, cy = (cam.Y + cam.H / 2) * sy;
            double aspect = (double)W / H;
            if (cw / ch < aspect) cw = ch * aspect; else ch = cw / aspect;
            cw = Math.Min(W, cw * 1.08); ch = Math.Min(H, ch * 1.08);
            double x0 = Math.Clamp(cx - cw / 2, 0, W - cw), y0 = Math.Clamp(cy - ch / 2, 0, H - ch);
            v.Add($"scale={W}:{H}:flags=bicubic,crop={(int)cw}:{(int)ch}:{(int)x0}:{(int)y0}");
        }
        v.Add($"scale={W}:{H}:flags=lanczos,setsar=1");

        // the zoom over time: punch-ins (eased), a slow push, room for whips and shake
        var z = new List<string> { "1" };
        if (Math.Abs(p.Push) > 0.001) z.Add($"{I(p.Push)}*t/{I(len)}");
        foreach (var zm in p.Zooms)
        {
            double a = zm.At, b = zm.At + zm.Len, ein = 0.16, eout = 0.22;
            // smoothstep up over ein, hold, back down over eout
            string up = $"clip((t-{I(a)})/{I(ein)},0,1)", dn = $"clip(({I(b)}-t)/{I(eout)},0,1)";
            z.Add($"{I(zm.Scale - 1)}*min({up}*{up}*(3-2*{up}),{dn}*{dn}*(3-2*{dn}))");
        }
        bool whip = p.WhipIn || p.WhipOut;
        if (whip || p.Shake) z.Add("0.06");
        string zoom = string.Join("+", z);
        bool moves = z.Count > 1;
        if (moves)
        {
            string dx = "0", dy = "0";
            var parts = new List<string>();
            if (p.WhipOut) parts.Add($"if(gte(t,{I(len - 0.2)}),-(iw-{W})/2*pow((t-{I(len - 0.2)})/0.2,2),0)");
            if (p.WhipIn) parts.Add($"if(lt(t,0.2),(iw-{W})/2*pow(1-t/0.2,2),0)");
            if (parts.Count > 0) dx = string.Join("+", parts);
            if (p.Shake) { dx += $"+if(lt(t,0.4),14*sin(t*95)*(1-t/0.4),0)"; dy = $"if(lt(t,0.4),10*cos(t*83)*(1-t/0.4),0)"; }
            v.Add($"scale=w='trunc({W}*({zoom})/2)*2':h='trunc({H}*({zoom})/2)*2':eval=frame:flags=bicubic");
            v.Add($"crop={W}:{H}:x='clip((iw-{W})/2+{dx},0,iw-{W})':y='clip((ih-{H})/2+{dy},0,ih-{H})'");
        }
        if (p.WhipOut) v.Add($"avgblur=sizeX=42:sizeY=1:enable='gte(t,{I(len - 0.17)})'");
        if (p.WhipIn) v.Add("avgblur=sizeX=42:sizeY=1:enable='lt(t,0.17)'");
        if (p.Blur) v.Add("gblur=sigma=22,eq=brightness=-0.1:saturation=0.75");
        if (p.Letterbox)
        {
            string bar = $"ih*0.105*min(1,t/0.45)";
            v.Add($"drawbox=x=0:y=0:w=iw:h='{bar}':color=black:t=fill,drawbox=x=0:y='ih-{bar}':w=iw:h='{bar}':color=black:t=fill");
        }
        if (p.Role == "slow") v.Add("drawbox=x=0:y=0:w=iw:h=ih:color=red@0.28:t=fill:enable='lt(t,0.1)',eq=saturation=0.7:contrast=1.08");
        v.Add("format=yuv420p");

        string audio;
        if (p.Mute) audio = $"anullsrc=r=48000:cl=stereo,atrim=duration={I(len)}";
        else
        {
            var af = new List<string> { "asetpts=PTS-STARTPTS", "aresample=48000", "aformat=channel_layouts=stereo" };
            if (p.Speed < 1) { af.Add($"atempo={I(Math.Max(0.5, p.Speed))}"); af.Add("lowpass=f=2400"); af.Add("volume=1.2"); }
            af.Add("apad");
            af.Add($"atrim=duration={I(len)}");
            af.Add("afade=t=in:d=0.012");
            af.Add($"afade=t=out:st={I(Math.Max(0, len - 0.015))}:d=0.015");
            audio = "[0:a]" + string.Join(",", af);
        }
        string graph = "[0:v]" + string.Join(",", v) + "[v];" + audio + "[a]";
        args.AddRange(new[] { "-filter_complex", graph, "-map", "[v]", "-map", "[a]", "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "15", "-g", "60", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2",
            "-t", I(len), output });
        return args;
    }

    // ------------------------------------------------------------------ the music + sound effects bed

    /// <summary>
    /// The score: each chapter's track, starting on the cut, stopping like a tape for a death, dipping under your
    /// voice (and under loud game moments), plus every sound effect at its cue.
    /// </summary>
    public static Dsp.Stereo Bed(EditPlan plan, VodSignals s)
    {
        double dur = plan.Duration + 0.5;
        var bed = new Dsp.Stereo(Dsp.Frames(dur));
        var music = new Dsp.Stereo(bed.Length);
        foreach (var span in plan.Music)
        {
            double len = span.To - span.From;
            var track = MusicSynth.Track(span.Mood, len + 1.5, span.Seed);
            int at = Dsp.Frames(span.From), n = Math.Min(track.Length, Dsp.Frames(len));
            // a quick fade in (it lands on the cut) and a short fade out into the next section's silence beat
            int fin = Dsp.Frames(0.03), fout = Dsp.Frames(span.TapeStopAtEnd ? 0.02 : 0.35);
            for (int i = 0; i < n && at + i < music.Length; i++)
            {
                float g = 1;
                if (i < fin) g = i / (float)fin;
                if (i > n - fout) g *= Math.Max(0, (n - i) / (float)fout);
                music.L[at + i] += track.L[i] * g;
                music.R[at + i] += track.R[i] * g;
            }
            if (span.TapeStopAtEnd) Sfx.TapeStop(music, Dsp.Frames(span.To - 0.6), 0.6);
        }

        // ducking: under your voice the music sits ~11 dB down, under other sound ~5 dB, alone (cards) it's full
        var spoken = new bool[(int)(dur * 20) + 1];
        foreach (var c in plan.Captions) for (double t = c.Start - 0.1; t < c.End + 0.15; t += 0.05) if (t >= 0 && (int)(t * 20) < spoken.Length) spoken[(int)(t * 20)] = true;
        double gain = 0.5;
        int step = Dsp.Rate / 200; // 5 ms
        for (int i = 0; i < music.Length; i += step)
        {
            double t = i / (double)Dsp.Rate;
            var piece = plan.Pieces.LastOrDefault(p => p.At <= t);
            double target = 0.5;
            if (piece is { Mute: false })
            {
                bool voice = spoken[Math.Min(spoken.Length - 1, (int)(t * 20))] || piece.Speed < 1;
                double src = piece.In + (t - piece.At) * piece.Speed;
                bool loud = s.LoudAt(src) > -28;
                target = voice ? 0.14 : loud ? 0.28 : 0.4;
            }
            double coef = target < gain ? 0.25 : 0.03; // fast down, slow back up
            for (int k = 0; k < step && i + k < music.Length; k++)
            {
                gain += (target - gain) * coef / step * 4;
                music.L[i + k] *= (float)gain;
                music.R[i + k] *= (float)gain;
            }
        }
        bed.Mix(music, 0);
        int seed = 1;
        foreach (var cue in plan.Sfx)
        {
            var fx = Sfx.Make(cue.Kind, seed++);
            bed.Mix(fx, Dsp.Frames(cue.At), cue.Gain);
        }
        return bed;
    }

    // ------------------------------------------------------------------ thumbnail and description

    private static async Task Thumbnail(string ffmpeg, string source, EditPlan plan, string output, CancellationToken ct)
    {
        string? font = Recap.FontFile();
        var words = plan.Title.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        string vf = "scale=1280:720:flags=lanczos,crop=1180:664,scale=1280:720,eq=contrast=1.18:saturation=1.35:brightness=0.02,vignette=angle=PI/4";
        if (font is not null && words.Length > 0)
        {
            string l1 = string.Join(' ', words.Take(2)), l2 = string.Join(' ', words.Skip(2));
            string f = VerticalClip.EscapeFilter(font);
            vf += $",drawtext=fontfile='{f}':text='{VerticalClip.EscapeFilter(l1)}':fontsize=118:fontcolor=white:borderw=10:bordercolor=black:x=60:y=h-{(l2.Length > 0 ? 300 : 190)}";
            if (l2.Length > 0) vf += $",drawtext=fontfile='{f}':text='{VerticalClip.EscapeFilter(l2)}':fontsize=118:fontcolor=0xFFD735:borderw=10:bordercolor=black:x=60:y=h-180";
        }
        string at = I(Math.Max(0, plan.BestMoment - 0.5));
        if (plan.Layout is { CanvasW: > 0, Camera: { } cam } lay)
        {
            // your face, big, on the right: the reaction sells the click
            int cx = (int)(cam.X * 1280 / lay.CanvasW), cy = (int)(cam.Y * 720 / lay.CanvasH), cw = (int)(cam.W * 1280 / lay.CanvasW), ch = (int)(cam.H * 720 / lay.CanvasH);
            string graph = $"[0:v]{vf}[bg];[0:v]scale=1280:720,crop={Math.Max(16, cw)}:{Math.Max(16, ch)}:{cx}:{cy},scale=-2:430,eq=contrast=1.12:saturation=1.25," +
                           "pad=iw+16:ih+16:8:8:color=white[face];[bg][face]overlay=x=W-w-36:y=36";
            await RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-ss", at, "-i", source, "-frames:v", "1", "-filter_complex", graph, "-q:v", "2", output }, ct);
            return;
        }
        await RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-ss", at, "-i", source, "-frames:v", "1", "-vf", vf, "-q:v", "2", output }, ct);
    }

    public static string Description(EditPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Title);
        sb.AppendLine();
        // YouTube chapters: start at 0:00, at least 10 s apart
        double last = -999;
        foreach (var (at, title) in plan.Chapters)
        {
            if (at - last < 10 && at > 0) continue;
            var ts = TimeSpan.FromSeconds(at);
            sb.AppendLine($"{(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"))} {title}");
            last = at;
        }
        sb.AppendLine();
        sb.AppendLine("Edited with Leapcast Studio AI. Music: original, generated for this video (no copyright).");
        return sb.ToString();
    }

    private static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            string e = (await err).Trim();
            throw new InvalidOperationException("FFmpeg: " + (e.Length > 400 ? e[^400..] : e));
        }
    }
}
