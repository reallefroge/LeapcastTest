using System.Globalization;

namespace LeapcastStudioAI;

public enum TimerMode { CountDown, CountUp, Clock }

/// <summary>
/// The stream timer: one per profile, driven from the Timer dock and drawn on the scene by any Timer source.
/// Counting is worked out from a start stamp plus whatever ran before, so it stays right across a pause, a scene
/// change or the app being restarted mid-stream.
/// </summary>
public sealed class StreamTimerState
{
    public string Title { get; set; } = "SUBATHON";
    public TimerMode Mode { get; set; } = TimerMode.CountDown;
    /// <summary>What a countdown starts from.</summary>
    public double DurationSeconds { get; set; } = 3600;
    /// <summary>At zero: keep counting into overtime (shown as -0:12) instead of sitting on 0:00.</summary>
    public bool Overtime { get; set; }
    /// <summary>Shown instead of 0:00 when a countdown finishes (blank keeps the zero).</summary>
    public string FinishedText { get; set; } = "TIME!";
    public bool Running { get; set; }
    /// <summary>Seconds counted before the current run.</summary>
    public double Banked { get; set; }
    public DateTime? StartedUtc { get; set; }

    public StreamTimerState Clone() => (StreamTimerState)MemberwiseClone();

    public double Counted(DateTime nowUtc) =>
        Banked + (Running && StartedUtc is { } s ? Math.Max(0, (nowUtc - s).TotalSeconds) : 0);

    /// <summary>Seconds on the clock: what's left on a countdown (negative in overtime), or time counted up.</summary>
    public double Value(DateTime nowUtc)
    {
        double c = Counted(nowUtc);
        if (Mode == TimerMode.CountUp) return c;
        double left = DurationSeconds - c;
        return Overtime ? left : Math.Max(0, left);
    }

    public bool Finished(DateTime nowUtc) => Mode == TimerMode.CountDown && DurationSeconds - Counted(nowUtc) <= 0;

    public void Start(DateTime nowUtc)
    {
        if (Running) return;
        Running = true; StartedUtc = nowUtc;
    }

    public void Pause(DateTime nowUtc)
    {
        if (!Running) return;
        Banked = Counted(nowUtc); Running = false; StartedUtc = null;
    }

    public void Reset(DateTime nowUtc)
    {
        Banked = 0; StartedUtc = Running ? nowUtc : null;
    }

    /// <summary>Adds time: a countdown gets longer, a count-up jumps forward. Never takes it below zero.</summary>
    public void Add(double seconds, DateTime nowUtc)
    {
        if (Mode == TimerMode.CountUp) Banked = Math.Max(0, Banked + seconds);
        else DurationSeconds = Math.Max(0, DurationSeconds + seconds);
    }

    /// <summary>What the source shows right now.</summary>
    public string Display(DateTime nowUtc, DateTime nowLocal)
    {
        if (Mode == TimerMode.Clock) return nowLocal.ToString("h:mm tt", CultureInfo.InvariantCulture).ToUpperInvariant();
        if (Mode == TimerMode.CountDown && !Overtime && Finished(nowUtc) && FinishedText.Trim().Length > 0)
            return FinishedText.Trim();
        return Clock(Value(nowUtc), ShowsHours);
    }

    /// <summary>True when the readout has an hours part, so the card is sized for it and never jumps width.</summary>
    public bool ShowsHours => Mode switch
    {
        TimerMode.Clock => false,
        TimerMode.CountUp => false,
        _ => DurationSeconds >= 3600,
    };

    /// <summary>The widest reading this timer can show, so the card is sized once and stays put.</summary>
    public string Widest(DateTime nowUtc)
    {
        if (Mode == TimerMode.Clock) return "12:00 PM";
        string longest = Mode == TimerMode.CountDown
            ? Clock(Math.Max(DurationSeconds, Value(nowUtc)), ShowsHours)
            : Clock(Math.Max(3599, Counted(nowUtc)), false);
        if (Mode == TimerMode.CountDown && Overtime) longest = "-" + longest;
        string fin = Mode == TimerMode.CountDown ? FinishedText.Trim() : "";
        return fin.Length > longest.Length ? fin : longest;
    }

    public static string Clock(double seconds, bool forceHours)
    {
        string sign = seconds < 0 ? "-" : "";
        var t = TimeSpan.FromSeconds(Math.Floor(Math.Abs(seconds) + 1e-6));
        int hours = (int)t.TotalHours;
        return hours > 0 || forceHours
            ? $"{sign}{hours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{sign}{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Reads "1:30:00", "90:00", "25m", "1h30", "600" (seconds) into a duration.</summary>
    public static bool TryParseDuration(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant();
        if (t.Contains(':'))
        {
            var bits = t.Split(':');
            if (bits.Length > 3) return false;
            double mul = 1, total = 0;
            for (int i = bits.Length - 1; i >= 0; i--)
            {
                if (!double.TryParse(bits[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
                total += v * mul; mul *= 60;
            }
            seconds = total; return seconds >= 0;
        }
        // "1h30" means an hour and a half: a number with no unit takes the next unit down from the last one given
        double acc = 0; bool any = false; string num = ""; double next = 1;
        foreach (char c in t + " ")
        {
            if (char.IsDigit(c) || c == '.') { num += c; continue; }
            if (num.Length > 0 && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                double unit = c == 'h' ? 3600 : c == 'm' ? 60 : c == 's' ? 1 : next;
                acc += v * unit; any = true; num = "";
                next = unit >= 3600 ? 60 : 1;
            }
        }
        seconds = acc;
        return any && seconds >= 0;
    }
}

/// <summary>
/// Draws the timer card on the scene. The plate, the title and the readout are all built from ffmpeg filters over a
/// transparent frame, and the digits come from a small text file that the engine rewrites as the clock runs
/// (drawtext re-reads it every frame), so the time ticks without the pipeline being rebuilt.
/// </summary>
public static class TimerCard
{
    public const string Stacked = "Stacked", Inline = "Inline", Plain = "No plate";
    public static readonly string[] Styles = { Stacked, Inline, Plain };

    public const string Plate = "0x14121F", Gold = "0xFFCF43", Ink = "0x0B0A12";

    /// <summary>A fixed-width face keeps the digits from shuffling as the seconds tick.</summary>
    private static readonly (string Path, double Advance)[] Faces =
    {
        (@"C:\Windows\Fonts\consolab.ttf", 0.550),
        (@"C:\Windows\Fonts\consola.ttf", 0.550),
        (@"C:\Windows\Fonts\lucon.ttf", 0.602),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", 0.602),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 0.602),
        ("/usr/share/fonts/dejavu/DejaVuSansMono-Bold.ttf", 0.602),
    };

    public static (string Path, double Advance)? Face() => Faces.Where(f => File.Exists(f.Path)).Select(f => ((string, double)?)f).FirstOrDefault();

    /// <summary>A title is set in spaced capitals, which reads better small and looks deliberate.</summary>
    public static string TitleText(string title) =>
        string.Join(" ", (title ?? "").Trim().ToUpperInvariant().ToCharArray());

    private static int Wide(string text, double size, double advance) =>
        (int)Math.Ceiling(text.Length * advance * size * 1.06 + 2);   // a little slack so nothing clips

    public sealed record Layout(int W, int H, double TimeSize, double TitleSize, int Pad, bool HasPlate);

    /// <param name="fontSize">The readout's size in canvas pixels.</param>
    /// <param name="widest">The longest reading the timer can show, so the card never changes width.</param>
    public static Layout Measure(string style, string title, string widest, double fontSize, double advance)
    {
        double fs = Math.Clamp(fontSize, 14, 400);
        double tfs = Math.Max(11, fs * 0.34);
        bool plate = style != Plain;
        string t = TitleText(title);
        int pad = (int)Math.Round(fs * (plate ? 0.34 : 0.10));
        int timeW = Wide(widest, fs, advance), titleW = t.Length > 0 ? Wide(t, tfs, advance) : 0;
        int timeLine = (int)Math.Round(fs * 1.22), titleLine = (int)Math.Round(tfs * 1.40);

        int w, h;
        if (style == Inline && titleW > 0)
        {
            int bar = (int)Math.Round(fs * 0.11), gapA = (int)Math.Round(fs * 0.26), gapM = (int)Math.Round(fs * 0.52);
            w = pad + bar + gapA + titleW + gapM + timeW + pad;
            h = (int)Math.Round(fs * 0.52) + Math.Max(timeLine, titleLine);
        }
        else
        {
            int gap = (int)Math.Round(fs * (plate ? 0.06 : 0.02));
            w = Math.Max(timeW, titleW) + pad * 2;
            h = pad * 2 + timeLine + (titleW > 0 ? titleLine + gap : 0);
        }
        return new Layout(Even(w), Even(h), fs, tfs, pad, plate);
    }

    private static int Even(int v) => v % 2 == 0 ? Math.Max(2, v) : v + 1;

    /// <summary>Boxes that chip the corners back to transparent, so the plate reads as rounded.</summary>
    private static string Round(int w, int h, double r, int steps = 4)
    {
        if (r < 2) return "";
        var parts = new List<string>();
        for (int i = 0; i < steps; i++)
        {
            double y0 = i * r / steps, y1 = (i + 1) * r / steps;
            double inset = r - Math.Sqrt(Math.Max(0, r * r - (r - y0) * (r - y0)));
            int cut = (int)Math.Ceiling(inset), sh = (int)Math.Ceiling(y1 - y0) + 1;
            if (cut < 1) continue;
            int top = (int)Math.Floor(y0);
            foreach (var (bx, by) in new[] { (0, top), (w - cut, top), (0, h - top - sh), (w - cut, h - top - sh) })
                parts.Add($"drawbox=x={bx}:y={by}:w={cut}:h={sh}:color=0x000000@0:t=fill:replace=1");
        }
        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    /// <summary>Where a piece of the card sits: 0 = left/top, 1 = centre, 2 = right, within the card.</summary>
    public enum Anchor { Start, Middle, End }

    /// <summary>
    /// One thing drawn on the card. A plate or bar uses X/Y/W/H; text uses X/Y as the anchor point with
    /// <see cref="AlignX"/> and <see cref="AlignY"/> saying what X/Y mean, so a renderer that measures text
    /// itself lands in the same place as FFmpeg's drawtext.
    /// </summary>
    public sealed record Piece(string Kind, double X, double Y, double W, double H, double Radius,
        string Text, string Colour, double Alpha, double Size, Anchor AlignX, Anchor AlignY, bool Outlined);

    public const string PlateKind = "plate", BarKind = "bar", TextKind = "text";

    /// <summary>
    /// The card as a list of pieces, for a renderer that draws it itself (the GPU canvas) rather than through
    /// FFmpeg. Both come from <see cref="Measure"/>, so the two can't drift apart in size or placement.
    /// </summary>
    public static IReadOnlyList<Piece> Pieces(Layout L, string style, string title, string reading, string colour = "#FFFFFF")
    {
        string t = TitleText(title);
        bool hasTitle = t.Length > 0;
        var ps = new List<Piece>();
        Piece Text(string text, double x, double y, double size, string col, Anchor ax, Anchor ay) =>
            new(TextKind, x, y, 0, 0, 0, text, col, 1, size, ax, ay, !L.HasPlate);

        if (L.HasPlate)
            ps.Add(new Piece(PlateKind, 0, 0, L.W, L.H, Math.Min(18, L.H * 0.22), "", Hex(Plate), 0.88, 0,
                Anchor.Start, Anchor.Start, false));

        if (style == Inline && hasTitle)
        {
            double bar = Math.Round(L.TimeSize * 0.11), inset = Math.Round(L.H * 0.22);
            ps.Add(new Piece(BarKind, L.Pad, inset, bar, L.H - inset * 2, 0, "", Hex(Gold), 1, 0,
                Anchor.Start, Anchor.Start, false));
            ps.Add(Text(t, L.Pad + bar + Math.Round(L.TimeSize * 0.26), L.H / 2.0 - 1, L.TitleSize, Hex(Gold),
                Anchor.Start, Anchor.Middle));
            ps.Add(Text(reading, L.W - L.Pad, L.H / 2.0, L.TimeSize, colour, Anchor.End, Anchor.Middle));
        }
        else if (hasTitle)
        {
            ps.Add(Text(t, L.W / 2.0, L.Pad, L.TitleSize, Hex(Gold), Anchor.Middle, Anchor.Start));
            ps.Add(Text(reading, L.W / 2.0,
                L.Pad + Math.Round(L.TitleSize * 1.40) + Math.Round(L.TimeSize * (L.HasPlate ? 0.06 : 0.02)),
                L.TimeSize, colour, Anchor.Middle, Anchor.Start));
        }
        else
            ps.Add(Text(reading, L.W / 2.0, L.H / 2.0, L.TimeSize, colour, Anchor.Middle, Anchor.Middle));

        return ps;
    }

    /// <summary>FFmpeg writes colours as 0xRRGGBB; a renderer on this side wants #RRGGBB.</summary>
    public static string Hex(string ffColour) =>
        ffColour.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "#" + ffColour[2..]
        : ffColour.StartsWith('#') ? ffColour : "#" + ffColour;

    /// <summary>
    /// The whole card as one filter chain, applied to a transparent frame of the measured size.
    /// </summary>
    /// <param name="timeFile">The file the engine keeps the current reading in.</param>
    public static string Filter(Layout L, string style, string title, string fontFile, string timeFile, string colour = "0xFFFFFF")
    {
        string f = VerticalClip.EscapeFilter(fontFile), tf = VerticalClip.EscapeFilter(timeFile);
        string t = VerticalClip.EscapeFilter(TitleText(title));
        bool hasTitle = TitleText(title).Length > 0;
        // the frame starts genuinely see-through: the colour source hands over opaque pixels otherwise
        var g = new List<string> { "format=rgba", $"drawbox=x=0:y=0:w={L.W}:h={L.H}:color=0x000000@0:t=fill:replace=1" };

        if (L.HasPlate)
            g.Add($"drawbox=x=0:y=0:w={L.W}:h={L.H}:color={Plate}@0.88:t=fill:replace=1"
                  + Round(L.W, L.H, Math.Min(18, L.H * 0.22)));

        string Time(string x, string y) =>
            $"drawtext=fontfile='{f}':textfile='{tf}':reload=1:fontcolor={colour}:fontsize={L.TimeSize:0.##}:" +
            $"x={x}:y={y}" + (L.HasPlate ? "" : $":borderw={Math.Max(2, L.TimeSize * 0.09):0.##}:bordercolor={Ink}@0.9") +
            $":shadowcolor={Ink}@0.55:shadowx=0:shadowy={Math.Max(1, L.TimeSize * 0.035):0.##}";

        string Title(string x, string y) =>
            $"drawtext=fontfile='{f}':text='{t}':fontcolor={Gold}:fontsize={L.TitleSize:0.##}:x={x}:y={y}"
            + (L.HasPlate ? "" : $":borderw={Math.Max(2, L.TitleSize * 0.14):0.##}:bordercolor={Ink}@0.9");

        if (style == Inline && hasTitle)
        {
            int bar = (int)Math.Round(L.TimeSize * 0.11), inset = (int)Math.Round(L.H * 0.22);
            g.Add($"drawbox=x={L.Pad}:y={inset}:w={bar}:h={L.H - inset * 2}:color={Gold}:t=fill");
            g.Add(Title($"{L.Pad + bar + (int)Math.Round(L.TimeSize * 0.26)}", "(h-text_h)/2-1"));
            g.Add(Time($"w-text_w-{L.Pad}", "(h-text_h)/2"));
        }
        else if (hasTitle)
        {
            g.Add(Title("(w-text_w)/2", $"{L.Pad}"));
            g.Add(Time("(w-text_w)/2", $"{L.Pad + (int)Math.Round(L.TitleSize * 1.40) + (int)Math.Round(L.TimeSize * (L.HasPlate ? 0.06 : 0.02))}"));
        }
        else
            g.Add(Time("(w-text_w)/2", "(h-text_h)/2"));

        return string.Join(",", g);
    }

    /// <summary>Keeps the reading on disk for drawtext, swapped in whole so a frame never catches half a write.</summary>
    public static void Write(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }
}
