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
        IReadOnlyList<string> videoArgs, VodSignals signals, IProgress<(double, string)>? progress, CancellationToken ct, string? pieceCache = null,
        Task? captionsReady = null, bool draft = false)
    {
        Directory.CreateDirectory(outputFolder);
        _fallbacks = 0;
        string work = Path.Combine(outputFolder, ".work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // ---- every piece with its effects, exactly its number of frames
            var list = new StringBuilder();
            // pieces are kept between revisions (named after exactly how they're made): an unchanged one isn't made again
            if (pieceCache is not null) Directory.CreateDirectory(pieceCache);
            // pieces are encoded on the graphics card when the stream's encoder is one and you're not live (the encode
            // is over half of a piece's time on the processor); every piece with the same encoder, so they join cleanly
            string enc = !BackgroundWork.Live && HardwareEncoderOf(videoArgs) is { } hw ? hw : "libx264";
            string[] Files(string e) => Enumerable.Range(0, plan.Pieces.Count).Select(i => pieceCache is null ? Path.Combine(work, $"p{i:0000}-{e}.mkv")
                : Path.Combine(pieceCache, "p-" + PieceKey(source, plan.Pieces[i], plan, e) + ".mkv")).ToArray();
            var files = Files(enc);
            int reused = 0;
            int done = 0;
            // full effects; if FFmpeg can't (or crashes), simpler effects; at worst the plain shot: the video always gets made
            async Task<bool> Make(int i, string e, string[] fs, CancellationToken token)
            {
                string partial = fs[i] + ".part.mkv";
                for (int level = 0; level <= 2; level++)
                {
                    try { await RunAsync(ffmpeg, PieceArgs(source, plan.Pieces[i], plan, partial, level, e), token); File.Move(partial, fs[i], true); return true; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { if (level < 2) Interlocked.Increment(ref _fallbacks); }
                }
                return false;
            }
            bool Cached(int i, string[] fs) => pieceCache is not null && File.Exists(fs[i]) && new FileInfo(fs[i]).Length > 0;
            if (enc != "libx264" && plan.Pieces.Count > 0 && !Cached(0, files) && !await Make(0, enc, files, ct))
            {
                Note($"pieces: {enc} couldn't make them here, using x264");
                enc = "libx264"; files = Files(enc);
            }
            bool hwBroken = false;
            // several pieces at once when you're not live (one, gently, while you are)
            await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, plan.Pieces.Count),
                new ParallelOptions { MaxDegreeOfParallelism = BackgroundWork.Parallel, CancellationToken = ct }, async (i, token) =>
                {
                    if (Cached(i, files) || (i == 0 && File.Exists(files[0])))
                    {
                        if (Cached(i, files)) Interlocked.Increment(ref reused);
                        int dd = Interlocked.Increment(ref done);
                        progress?.Report((0.05 + 0.75 * dd / plan.Pieces.Count, $"Editing: piece {dd} of {plan.Pieces.Count}{(Cached(i, files) ? " (unchanged)" : "")}"));
                        return;
                    }
                    if (!await Make(i, enc, files, token))
                    {
                        if (enc == "libx264") throw new InvalidOperationException($"Piece {i + 1} couldn't be made.");
                        hwBroken = true;
                    }
                    int d = Interlocked.Increment(ref done);
                    progress?.Report((0.05 + 0.75 * d / plan.Pieces.Count, $"Editing: piece {d} of {plan.Pieces.Count}"));
                });
            if (hwBroken)
            {
                // a piece the graphics card couldn't encode: all of them again on the processor (one encoder for the whole video)
                Note($"pieces: {enc} failed on a piece, making them all with x264");
                enc = "libx264"; files = Files(enc);
                await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, plan.Pieces.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = BackgroundWork.Parallel, CancellationToken = ct }, async (i, token) =>
                    {
                        if (!Cached(i, files) && !await Make(i, enc, files, token)) throw new InvalidOperationException($"Piece {i + 1} couldn't be made.");
                    });
            }
            PieceEncoder = enc;
            Reused = reused;
            foreach (var file in files) list.AppendLine($"file '{file.Replace("\\", "/").Replace("'", "'\\''")}'");
            string listFile = Path.Combine(work, "pieces.txt");
            await File.WriteAllTextAsync(listFile, list.ToString(), ct);
            string body = Path.Combine(work, "body.mkv");
            await RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", body }, ct);

            // ---- music + sound effects, ducked under the stream's own sound
            // the captions were written while the pieces rendered: the music ducks under them and the text needs them
            if (captionsReady is not null) { progress?.Report((0.81, "Finishing the captions")); await captionsReady; }
            progress?.Report((0.82, "Scoring the music"));
            string bed = Path.Combine(work, "bed.wav");
            await Task.Run(() => Dsp.WriteWav(bed, Bed(plan, signals, ffmpeg)), ct);

            // ---- text, grade, mix, final encode
            progress?.Report((0.86, "Final render"));
            string ass = Path.Combine(work, "text.ass");
            await File.WriteAllTextAsync(ass, AssWriter.Build(plan), new UTF8Encoding(false), ct);
            string output = Unique(Path.Combine(outputFolder, baseName + ".mp4"));
            // even loudness: big swings inside a part are smoothed too (slowly: no pumping)
            string mix = "[0:a]highpass=f=70," + (plan.EvenLoudness ? "dynaudnorm=f=250:g=21:p=0.9:m=6:s=8," : "") +
                         "acompressor=threshold=0.12:ratio=3:attack=8:release=180:makeup=1.6[p];" +
                         "[1:a]equalizer=f=2600:t=q:w=1.3:g=-3[b];[p][b]amix=inputs=2:normalize=0:duration=first";
            // loudness in two passes (measure, then apply exactly): YouTube plays everything at -14 LUFS
            string norm = "loudnorm=I=-14:TP=-1.5:LRA=11";
            if (!draft)
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
            // the full finish; if that fails: the stream's encoder swapped for x264, then without the grade
            var x264 = new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p" };
            var tries = new (string Video, IReadOnlyList<string> Enc)[]
            {
                ($"eq=contrast=1.05:saturation=1.12:gamma=0.98,vignette=angle=PI/5,{AssFilter(ass)},format=yuv420p", videoArgs),
                ($"eq=contrast=1.05:saturation=1.12:gamma=0.98,vignette=angle=PI/5,{AssFilter(ass)},format=yuv420p", x264),
                ($"{AssFilter(ass)},format=yuv420p", x264),
            };
            for (int t = 0; ; t++)
            {
                var args = new List<string> { "-y", "-v", "error", "-i", body, "-i", bed, "-filter_complex",
                    $"[0:v]{tries[t].Video}[v];" + mix + $",{norm},aresample=48000[a]", "-map", "[v]", "-map", "[a]" };
                args.AddRange(tries[t].Enc);
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output });
                try { await RunAsync(ffmpeg, args, ct); break; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (t + 1 < tries.Length) { Interlocked.Increment(ref _fallbacks); }
            }

            if (draft) { progress?.Report((1, "Done")); return new Result(output, "", "", TimeSpan.FromSeconds(plan.Duration)); }
            // ---- thumbnail and description
            progress?.Report((0.97, "Thumbnail and chapters"));
            string thumb = Path.ChangeExtension(output, ".jpg");
            try { await Thumbnail(ffmpeg, source, plan, thumb, ct); }
            catch (OperationCanceledException) { throw; }
            catch { thumb = ""; } // the video is what matters
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

    /// <summary>The text overlay, with the app's own fonts (Cinzel) available to it.</summary>
    private static string AssFilter(string ass) =>
        FontsDir is { } fd && Directory.Exists(fd) ? $"ass='{VerticalClip.EscapeFilter(ass)}':fontsdir='{VerticalClip.EscapeFilter(fd)}'" : $"ass='{VerticalClip.EscapeFilter(ass)}'";

    /// <summary>The app's fonts (Cinzel, Cinzel Decorative: SIL Open Font License), next to the program.</summary>
    public static string? FontsDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    private static int StableHash(string s) { unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h; } }

    // ------------------------------------------------------------------ pieces

    /// <summary>The ffmpeg command for one piece: trim, slow motion, face crop, punch-ins, whips, shake, letterbox, blur.</summary>
    /// <summary>How many pieces had to be made with simpler effects (logged).</summary>
    private static int _fallbacks;
    public static int Fallbacks => _fallbacks;

    /// <param name="level">0 = every effect; 1 = no frame blending or moving zoom; 2 = just the shot.</param>
    /// <param name="encoder">The pieces' encoder: x264, or the graphics card's (near-lossless either way: they're re-encoded at the end).</param>
    public static List<string> PieceArgs(string source, Piece p, EditPlan plan, string output, int level = 0, string encoder = "libx264")
    {
        int W = plan.Width, H = plan.Height, frames = plan.FramesOf(p);
        double F = plan.Fps, len = frames / F;
        var args = new List<string> { "-y", "-v", "error" };
        // a timelapse decodes a lot of footage for a few seconds: on the graphics card (and only keyframes when it's very fast)
        if (p.Speed > 1) args.AddRange(new[] { "-hwaccel", "auto" });
        if (p.Speed >= 40) args.AddRange(new[] { "-skip_frame", "nokey" });
        if (p.Hold > 0) args.AddRange(new[] { "-ss", I(Math.Max(0, p.In)), "-t", "0.2" });
        else args.AddRange(new[] { "-ss", I(p.In), "-t", I(p.Out - p.In + 0.1) });
        args.AddRange(new[] { "-i", p.Source ?? source });
        var ov = level < 2 ? p.Overlay : null;
        if (ov is not null) args.AddRange(new[] { "-hwaccel", "auto", "-ss", I(ov.In), "-t", I(ov.Out - ov.In + 0.2), "-i", source });
        var v = new List<string>();
        v.Add(Math.Abs(p.Speed - 1) > 1e-6 ? $"setpts=(PTS-STARTPTS)/{I(p.Speed)}" : "setpts=PTS-STARTPTS");
        if (p.Hold > 0) v.Add($"tpad=stop_mode=clone:stop_duration={I(p.Hold + 1)}");
        if (p.Speed < 1 && level == 0) v.Add($"minterpolate=fps={I(F)}:mi_mode=blend");
        v.Add($"fps={I(F)}");
        if (p.Crop is { } cam && plan.Layout is { CanvasW: > 0 } lay && level < 2)
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
        // a push in grows from 1; a pull out starts in close and eases back to 1 (never smaller than the frame:
        // a frame smaller than its crop crashes FFmpeg)
        if (p.Push > 0.001) z.Add($"{I(p.Push)}*t/{I(len)}");
        else if (p.Push < -0.001) z.Add($"{I(-p.Push)}*(1-t/{I(len)})");
        foreach (var zm in p.Zooms)
        {
            double a = zm.At, b = zm.At + zm.Len, ein = 0.16, eout = 0.22;
            // smoothstep up over ein, hold, back down over eout
            string up = $"clip((t-{I(a)})/{I(ein)},0,1)", dn = $"clip(({I(b)}-t)/{I(eout)},0,1)";
            z.Add($"{I(zm.Scale - 1)}*min({up}*{up}*(3-2*{up}),{dn}*{dn}*(3-2*{dn}))");
        }
        bool whip = p.WhipIn || p.WhipOut;
        if (whip || p.Shake) z.Add("0.06");
        // zoom punch transitions: rushing in at the end of a clip, settling from close at the start of the next
        const double edge = 0.22;
        if (p.TransIn == TransitionKinds.Zoom) z.Add($"0.3*pow(clip(1-t/{I(edge)},0,1),2)");
        if (p.TransOut == TransitionKinds.Zoom) z.Add($"0.3*pow(clip((t-{I(len - edge)})/{I(edge)},0,1),2)");
        string zoom = string.Join("+", z);
        bool moves = z.Count > 1 && level == 0;
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
        if (level < 2)
        {
            if (p.WhipOut) v.Add($"avgblur=sizeX=42:sizeY=1:enable='gte(t,{I(len - 0.17)})'");
            if (p.WhipIn) v.Add("avgblur=sizeX=42:sizeY=1:enable='lt(t,0.17)'");
            if (p.Letterbox)
            {
                string bar = $"ih*0.105*min(1,t/0.45)";
                v.Add($"drawbox=x=0:y=0:w=iw:h='{bar}':color=black:t=fill,drawbox=x=0:y='ih-{bar}':w=iw:h='{bar}':color=black:t=fill");
            }
            if (p.Role == "slow") v.Add("drawbox=x=0:y=0:w=iw:h=ih:color=red@0.28:t=fill:enable='lt(t,0.1)',eq=saturation=0.7:contrast=1.08");
            // the transitions' looks at this clip's edges
            string inWin = $"lt(t,{I(edge)})", outWin = $"gte(t,{I(len - edge)})";
            foreach (var (style, isIn) in new[] { (p.TransIn, true), (p.TransOut, false) })
            {
                string win = isIn ? inWin : outWin;
                switch (style)
                {
                    case TransitionKinds.Fade: v.Add(isIn ? $"fade=t=in:st=0:d={I(edge)}" : $"fade=t=out:st={I(len - edge)}:d={I(edge)}"); break;
                    case TransitionKinds.Flash: v.Add(isIn ? "fade=t=in:st=0:d=0.18:color=white" : $"fade=t=out:st={I(len - 0.12)}:d=0.12:color=white"); break;
                    case TransitionKinds.Blur: v.Add($"gblur=sigma=18:enable='{win}'"); break;
                    case TransitionKinds.Glitch:
                        string g = isIn ? "lt(t,0.15)" : $"gte(t,{I(len - 0.15)})";
                        v.Add($"rgbashift=rh=-12:bh=12:gv=4:enable='{g}',noise=alls=40:allf=t:enable='{g}'");
                        break;
                }
            }
            // a VAR check: the clip dims while the replay is up
            if (ov is not null) v.Add($"eq=brightness=-0.12:saturation=0.55:enable='between(t,{I(ov.Start)},{I(ov.Start + ov.Length)})'");
        }
        if (p.Blur) v.Add("gblur=sigma=22,eq=brightness=-0.1:saturation=0.75");
        if (p.Darken) v.Add("eq=brightness=-0.14:saturation=0.85,vignette=angle=PI/4");
        v.Add("format=yuv420p");

        string audio;
        if (p.Mute) audio = $"anullsrc=r=48000:cl=stereo,atrim=duration={I(len)}";
        else
        {
            var af = new List<string> { "asetpts=PTS-STARTPTS", "aresample=48000", "aformat=channel_layouts=stereo" };
            if (Math.Abs(p.GainDb) >= 0.5) af.Add($"volume={I(p.GainDb)}dB");
            if (p.Speed < 1) { af.Add($"atempo={I(Math.Max(0.5, p.Speed))}"); af.Add("lowpass=f=2400"); af.Add("volume=1.2"); }
            af.Add("apad");
            af.Add($"atrim=duration={I(len)}");
            // a fade fades the sound too; otherwise just enough to never click
            double ain = p.TransIn == TransitionKinds.Fade ? edge : 0.012, aout = p.TransOut == TransitionKinds.Fade ? edge : 0.015;
            af.Add($"afade=t=in:d={I(ain)}");
            af.Add($"afade=t=out:st={I(Math.Max(0, len - aout))}:d={I(aout)}");
            audio = "[0:a]" + string.Join(",", af);
        }
        string graph = "[0:v]" + string.Join(",", v) + "[v];" + audio + "[a]";
        if (ov is null && p.Image is { } img && File.Exists(img) && level < 2)
        {
            // your logo: rises and fades in over the intro, fades out with it
            args.InsertRange(args.IndexOf("-i") + 2, new[] { "-loop", "1", "-t", I(len + 0.5), "-i", img });
            int lh = (int)(H * 0.30) & ~1;
            var main = v.Take(v.Count - 1);
            graph = "[0:v]" + string.Join(",", main) + "[m];" +
                    $"[1:v]scale=-2:{lh}:flags=lanczos,format=rgba,fade=t=in:st=0.3:d=0.4:alpha=1,fade=t=out:st={I(Math.Max(0.8, len - 0.35))}:d=0.3:alpha=1[lg];" +
                    $"[m][lg]overlay=x=(W-w)/2:y='{(int)(H * 0.11)}+24*max(0,1-(t-0.3)/0.5)':eof_action=pass,format=yuv420p[v];" + audio + "[a]";
        }
        else if (ov is not null)
        {
            // the replay: your round, sped up to fit, in a white frame that slides in from the right
            double sp = Math.Clamp((ov.Out - ov.In) / ov.Length, 1, 60);
            int ow = (W * 44 / 100) & ~1, oh = (ow * H / W) & ~1, x0 = W - ow - 8 - (int)(W * 0.03), y0 = (int)(H * 0.2);
            string slide = $"if(lt(t-{I(ov.Start)},0.3),{W}-({W}-{x0})*(t-{I(ov.Start)})/0.3,{x0})";
            var main = v.Take(v.Count - 1);   // (the final format comes after the overlay)
            graph = "[0:v]" + string.Join(",", main) + "[m];" +
                    $"[1:v]setpts=(PTS-STARTPTS)/{I(sp)}+{I(ov.Start)}/TB,fps={I(F)},scale={ow}:{oh}:flags=bicubic,pad={ow + 8}:{oh + 8}:4:4:color=white[o];" +
                    $"[m][o]overlay=x='{slide}':y={y0}:eof_action=pass:enable='between(t,{I(ov.Start)},{I(ov.Start + ov.Length)})',format=yuv420p[v];" + audio + "[a]";
        }
        args.AddRange(new[] { "-filter_complex", graph, "-map", "[v]", "-map", "[a]", "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
            "-threads", BackgroundWork.Threads.ToString(CultureInfo.InvariantCulture),
        });
        args.AddRange(PieceVideo(encoder));
        args.AddRange(new[] { "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-t", I(len), output });
        return args;
    }

    /// <summary>The intermediates' video encode: high quality (they're re-encoded once at the end), as fast as possible.</summary>
    public static string[] PieceVideo(string encoder) => encoder switch
    {
        "h264_amf" => new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "speed", "-rc", "cqp", "-qp_i", "16", "-qp_p", "17", "-g", "60" },
        "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p2", "-rc", "constqp", "-qp", "16", "-bf", "0", "-g", "60" },
        "h264_qsv" => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "17", "-g", "60" },
        _ => new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "15", "-g", "60" },
    };

    /// <summary>The graphics-card encoder in the stream's video settings, if it uses one.</summary>
    public static string? HardwareEncoderOf(IReadOnlyList<string> videoArgs)
    {
        for (int i = 0; i + 1 < videoArgs.Count; i++)
            if (videoArgs[i] == "-c:v" && videoArgs[i + 1] is "h264_amf" or "h264_nvenc" or "h264_qsv") return videoArgs[i + 1];
        return null;
    }

    /// <summary>The encoder the last render's pieces were made with (logged).</summary>
    public static string PieceEncoder { get; private set; } = "libx264";

    // ------------------------------------------------------------------ the music + sound effects bed

    /// <summary>
    /// The score: each chapter's track, starting on the cut, stopping like a tape for a death, dipping under your
    /// voice (and under loud game moments), plus every sound effect at its cue.
    /// </summary>
    public static Dsp.Stereo Bed(EditPlan plan, VodSignals s, string? ffmpeg = null)
    {
        double dur = plan.Duration + 0.5;
        var bed = new Dsp.Stereo(Dsp.Frames(dur));
        var music = new Dsp.Stereo(bed.Length);
        // your music (a folder of tracks: a different one per chapter, never twice in a video) or music made for the video
        var library = plan.Brand.MusicSource == "folder" && ffmpeg is not null ? MusicLibrary.Tracks(plan.Brand.MusicFolder) : new List<string>();
        int rotate = library.Count == 0 ? 0 : (int)((uint)StableHash(plan.Title + plan.Duration.ToString("0")) % (uint)library.Count);
        int spanNo = 0;
        foreach (var span in plan.Music)
        {
            double len = span.To - span.From;
            Dsp.Stereo? track = library.Count > 0 ? MusicLibrary.Decode(ffmpeg!, library[(rotate + spanNo++) % library.Count], 0, len + 1.5) : null;
            if (plan.Brand.MusicSource == "none") continue;
            // your music only: no folder yet (or a track that won't play) means silence there, never made-up music instead
            if (plan.Brand.MusicSource == "folder" && track is null) continue;
            track ??= MusicSynth.Track(span.Style, len + 1.5, span.Seed);
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

        // the intro's own music: the same track every video (your intro track, else your first track's opening)
        if (plan.Pieces.FirstOrDefault(p => p.Role == "title") is { } intro && plan.Brand.MusicSource != "none" && ffmpeg is not null)
        {
            string? theme = plan.Brand.IntroMusic is { Length: > 0 } im && File.Exists(im) ? im : library.FirstOrDefault();
            if (theme is not null && MusicLibrary.Decode(ffmpeg, theme, 0, intro.Length + 1.2) is { } t)
            {
                int a = Dsp.Frames(Math.Max(0, intro.At - 0.15)), n = Math.Min(t.Length, music.Length - a), fo = Dsp.Frames(0.6), fi = Dsp.Frames(0.05);
                // (the chapter music steps aside for it)
                for (int i = 0; i < n; i++)
                {
                    float duck = i < fi ? 1 - i / (float)fi : i > n - fo ? (i - (n - fo)) / (float)fo : 0;
                    float g = i > n - fo ? Math.Max(0, (n - i) / (float)fo) : 1;
                    music.L[a + i] = music.L[a + i] * duck + t.L[i] * g * 1.6f;
                    music.R[a + i] = music.R[a + i] * duck + t.R[i] * g * 1.6f;
                }
            }
        }

        // (MusicLevel: everything below, halved again on request: the music is a backdrop, not the show)
        // the music serves the story: a quiet bed under your voice (≈ -22 dB), almost gone once you've been talking a
        // while, a little more under game sound, and only up front on its own (title, end card)
        var spoken = new bool[(int)(dur * 20) + 1];
        foreach (var c in plan.Captions) for (double t = c.Start - 0.15; t < c.End + 0.4; t += 0.05) if (t >= 0 && (int)(t * 20) < spoken.Length) spoken[(int)(t * 20)] = true;
        double gain = 0.35, talking = 0;
        int step = Dsp.Rate / 200; // 5 ms
        for (int i = 0; i < music.Length; i += step)
        {
            double t = i / (double)Dsp.Rate;
            var piece = plan.Pieces.LastOrDefault(p => p.At <= t);
            double target = 0.35;
            if (piece is { Mute: false })
            {
                bool voice = spoken[Math.Min(spoken.Length - 1, (int)(t * 20))];
                talking = voice ? talking + 0.005 : Math.Max(0, talking - 0.01);
                double src = piece.In + (t - piece.At) * piece.Speed;
                // anyone talking (your friends on Discord too, not just you) or the game making noise: the music
                // steps back; it only comes up a little in real quiet
                float lvl = Math.Max(s.LoudAt(src), s.LoudAt(src + 0.1));
                bool sound = lvl > -38, loud = lvl > -28;
                target = piece.Speed < 1 ? 0.05 : voice ? (talking > 6 ? 0.03 : 0.055) : loud ? 0.06 : sound ? 0.08 : 0.14;
            }
            else talking = 0;
            double coef = target < gain ? 0.25 : 0.02; // quick to get out of the way, slow to come back
            for (int k = 0; k < step && i + k < music.Length; k++)
            {
                gain += (target - gain) * coef / step * 4;
                music.L[i + k] *= (float)(gain * MusicLevel * plan.MusicVolume);
                music.R[i + k] *= (float)(gain * MusicLevel * plan.MusicVolume);
            }
        }
        bed.Mix(music, 0);
        int seed = 1;
        foreach (var cue in plan.Sfx)
        {
            var fx = Sfx.Make(cue.Kind, cue.Seed != 0 ? cue.Seed : seed++);
            if (plan.SfxVolume <= 0.01) continue;
            bed.Mix(fx, Dsp.Frames(cue.At), cue.Gain * 0.75f * (float)plan.SfxVolume); // accents, not punches
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
            // the camera's spot, kept inside the picture (a layout may run it past the edge)
            int cx = Math.Clamp((int)(cam.X * 1280 / lay.CanvasW), 0, 1264), cy = Math.Clamp((int)(cam.Y * 720 / lay.CanvasH), 0, 704);
            int cw = Math.Clamp((int)(cam.W * 1280 / lay.CanvasW), 16, 1280 - cx), ch = Math.Clamp((int)(cam.H * 720 / lay.CanvasH), 16, 720 - cy);
            string graph = $"[0:v]{vf}[bg];[0:v]scale=1280:720,crop={cw}:{ch}:{cx}:{cy},scale=-2:430,eq=contrast=1.12:saturation=1.25," +
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
        sb.AppendLine(plan.Brand.MusicSource switch
        {
            "folder" when MusicLibrary.Tracks(plan.Brand.MusicFolder).Count > 0 => "Edited with Leapcast Studio AI.",
            "none" => "Edited with Leapcast Studio AI.",
            _ => "Edited with Leapcast Studio AI. Music: original, generated for this video (no copyright).",
        });
        if (plan.Texts.Any(t => t.Kind == "endscreen"))
        {
            var end = plan.Texts.First(t => t.Kind == "endscreen");
            sb.AppendLine();
            sb.AppendLine("---- (not for the description) End screen, in YouTube Studio → Editor → End screen:");
            sb.AppendLine($"It starts at {TimeSpan.FromSeconds(end.At - 0.3):m\\:ss} and lasts 20 s. Add three elements and drag each onto its box:");
            sb.AppendLine("  • Video → \"Most recent upload\" (or pick your previous video) → the left box (PREVIOUS VIDEO)");
            sb.AppendLine("  • Video → \"Best for viewer\" (or pick one) → the right box (WATCH NEXT)");
            sb.AppendLine("  • Subscribe → the circle (SUBSCRIBE)");
            sb.AppendLine("Tip: after the first video, use \"Import from video\" to copy the same end screen in one click.");
        }
        return sb.ToString();
    }

    /// <summary>The music's overall level (0.32 of the original mix: about −10 dB everywhere; lowered twice on request).</summary>
    public const double MusicLevel = 0.32;

    /// <summary>A name for a piece made exactly this way (source, timing, every effect).</summary>
    private static string PieceKey(string source, Piece p, EditPlan plan, string encoder = "libx264")
    {
        var args = PieceArgs(source, p, plan, "OUT", 0, encoder).ToList();
        int th = args.IndexOf("-threads"); // how many threads made it doesn't change the picture
        if (th >= 0) args.RemoveRange(th, 2);
        string recipe = string.Join("\u0001", args) + $"|{new FileInfo(source).Length}";
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(recipe)))[..20].ToLowerInvariant();
    }

    /// <summary>Pieces reused from the last render (a revision).</summary>
    public static int Reused { get; private set; }

    /// <summary>A line in the edit log (how long each step took, what fell back).</summary>
    public static void Note(string line)
    {
        if (LogPath is not { } log) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>Where every failed FFmpeg command is written (with its full output), when set.</summary>
    public static string? LogPath { get; set; }

    /// <summary>The last failed FFmpeg command and its full error output (for the log).</summary>
    public static string? LastFailure { get; private set; }

    private static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        BackgroundWork.Track(p);
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            string e = (await err).Trim();
            LastFailure = "ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') || a.Contains(';') ? $"\"{a}\"" : a)) + Environment.NewLine + e;
            LastFailure = $"exit code {p.ExitCode}{Environment.NewLine}" + LastFailure;
            if (LogPath is { } log)
                try { File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LastFailure}{Environment.NewLine}{Environment.NewLine}"); } catch { }
            string why = e.Length > 0 ? (e.Length > 400 ? e[^400..] : e)
                : $"FFmpeg stopped without a message (exit code {p.ExitCode}{(p.ExitCode < 0 || p.ExitCode > 255 ? ", it crashed" : "")})";
            throw new InvalidOperationException("FFmpeg: " + why);
        }
    }
}

/// <summary>Your music: the audio files in a folder (e.g. a game's soundtrack you have), read with FFmpeg.</summary>
public static class MusicLibrary
{
    private static readonly string[] Extensions = { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".opus", ".wma", ".aac" };

    /// <summary>The folder's tracks, in name order.</summary>
    public static List<string> Tracks(string? folder)
    {
        try
        {
            return folder is { Length: > 0 } && Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Seconds <paramref name="start"/>…+<paramref name="length"/> of a track (looped if it's shorter), 48 kHz stereo; null if it won't play.</summary>
    public static Dsp.Stereo? Decode(string ffmpeg, string file, double start, double length)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-stream_loop", "-1", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-vn",
                         "-t", length.ToString("0.###", CultureInfo.InvariantCulture), "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1" })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(30000);
            var bytes = ms.GetBuffer();
            int frames = (int)(ms.Length / 8);
            if (frames < 4800) return null;
            var st = new Dsp.Stereo(frames);
            for (int i = 0; i < frames; i++)
            {
                st.L[i] = BitConverter.ToSingle(bytes, i * 8);
                st.R[i] = BitConverter.ToSingle(bytes, i * 8 + 4);
            }
            return st;
        }
        catch { return null; }
    }
}
