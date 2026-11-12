using System.Globalization;
using System.Text;

namespace LeapcastStudioAI.Vod;

/// <summary>
/// All on-screen text as one animated subtitle file (rendered by libass): word-by-word captions that pop on the
/// word being said, the title card, chapter cards that slide in, "later" tags and the end card.
/// </summary>
public static class AssWriter
{
    // ASS colours are &HAABBGGRR
    private const string Yellow = "&H0035D7FF&", Hot = "&H003C6EFF&", Accent = "&H00FF9C3D&", White = "&H00FFFFFF&";
    // the Wizard101-style look: old gold, parchment, deep night blue (colours and a serif, not the game's own art)
    private const string Gold = "&H006AC2E8&", Parchment = "&H00C4E6F3&", Night = "&H00321014&", NightA = "&H40321014&", GoldDim = "&H004A8FB0&";
    private const string Display = "Cinzel Decorative Black", Serif = "Cinzel";

    private static string T(double s)
    {
        if (s < 0) s = 0;
        int cs = (int)Math.Round(s * 100);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    // ---- vector shapes (libass drawings): filled rectangles, a frame of four bars, a diamond, a ring of quads
    private static string Rect(int w, int h) => $"m 0 0 l {w} 0 {w} {h} 0 {h}";
    private static string Frame(int w, int h, int t) =>
        $"m 0 0 l {w} 0 {w} {t} 0 {t} m 0 {h - t} l {w} {h - t} {w} {h} 0 {h} m 0 {t} l {t} {t} {t} {h - t} 0 {h - t} m {w - t} {t} l {w} {t} {w} {h - t} {w - t} {h - t}";
    private static string Diamond(int r) => $"m 0 {-r} l {r} 0 0 {r} {-r} 0";
    private static string Ring(int r, int t, int n = 64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
            (int, int) P(double a, int rr) => ((int)Math.Round(rr * Math.Cos(a)), (int)Math.Round(rr * Math.Sin(a)));
            var (x0, y0) = P(a0, r); var (x1, y1) = P(a1, r); var (x2, y2) = P(a1, r - t); var (x3, y3) = P(a0, r - t);
            sb.Append($"m {x0} {y0} l {x1} {y1} {x2} {y2} {x3} {y3} ");
        }
        return sb.ToString().Trim();
    }

    public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("{", "(").Replace("}", ")").Replace("\n", "\\N");

    public static string Build(EditPlan plan, string font = "Segoe UI Black", string bodyFont = "Segoe UI Semibold")
    {
        int W = plan.Width, H = plan.Height;
        int cap = (int)(H * 0.062), outline = Math.Max(3, (int)(H * 0.0058));
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {W}");
        sb.AppendLine($"PlayResY: {H}");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        string Style(string name, string f, int size, string primary, int border, int shadow, int align, int marginV, int spacing = 0, int borderStyle = 1, string back = "&H8C000000")
            => $"Style: {name},{f},{size},{primary},&H000000FF,&H00000000,{back},-1,0,0,0,100,100,{spacing},0,{borderStyle},{border},{shadow},{align},{(int)(W * 0.06)},{(int)(W * 0.06)},{marginV},1";
        sb.AppendLine(Style("Cap", font, cap, White, outline, 2, 2, (int)(H * 0.13), 1));
        sb.AppendLine(Style("Title", font, (int)(H * 0.105), White, Math.Max(3, (int)(H * 0.005)), 3, 5, 0, 2));
        sb.AppendLine(Style("Sub", bodyFont, (int)(H * 0.034), "&H00E6E6E6", 2, 1, 5, 0, 8));
        sb.AppendLine(Style("Label", font, (int)(H * 0.03), Accent, 2, 1, 4, 0, 6));
        sb.AppendLine(Style("Quote", bodyFont, (int)(H * 0.048), White, Math.Max(2, outline - 2), 2, 4, 0));
        sb.AppendLine(Style("Tag", font, (int)(H * 0.03), White, (int)(H * 0.012), 0, 7, (int)(H * 0.06), 4, 3, "&H33101018"));
        sb.AppendLine(Style("Bar", "Arial", 10, Yellow, 0, 0, 7, 0));
        sb.AppendLine(Style("WTitle", Display, (int)(H * 0.1), Gold, Math.Max(3, (int)(H * 0.004)), 2, 5, 0, 4).Replace(",&H00000000,", $",{Night},"));
        sb.AppendLine(Style("WSub", Serif, (int)(H * 0.036), Parchment, 2, 1, 5, 0, 10).Replace(",&H00000000,", $",{Night},"));
        sb.AppendLine(Style("WSmall", Serif, (int)(H * 0.024), Parchment, 2, 0, 5, 0, 6).Replace(",&H00000000,", $",{Night},"));
        sb.AppendLine(Style("WLabel", Display, (int)(H * 0.04), Gold, 2, 1, 4, 0, 3).Replace(",&H00000000,", $",{Night},"));
        sb.AppendLine(Style("WQuote", Serif, (int)(H * 0.052), Parchment, Math.Max(3, (int)(H * 0.004)), 2, 2, 0, 1).Replace(",&H00000000,", $",{Night},"));
        sb.AppendLine(Style("WTag", Serif, (int)(H * 0.03), Gold, (int)(H * 0.012), 0, 7, 0, 4, 3, NightA));
        sb.AppendLine(Style("Shape", "Arial", 10, Gold, 0, 0, 7, 0));
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        void Ev(int layer, double a, double b, string style, string text) => sb.AppendLine($"Dialogue: {layer},{T(a)},{T(b)},{style},,0,0,0,,{text}");

        // ---- captions: a few words at a time, the word being said pops and lights up
        var groups = new List<List<CaptionCue>>();
        foreach (var c in plan.Captions.OrderBy(c => c.Start))
        {
            var g = groups.Count > 0 ? groups[^1] : null;
            bool fresh = g is null || g.Count >= 3 || c.Start - g[^1].End > 0.45 || string.Join(' ', g.Select(x => x.Text)).Length + c.Text.Length > 18;
            if (fresh) groups.Add(new List<CaptionCue> { c }); else g!.Add(c);
        }
        // no captions under the title, chapter or end cards
        var blocked = plan.Texts.Where(t => t.Kind is "title" or "end" or "endscreen" or "chapter" or "hook").Select(t => (t.At - 0.1, t.At + t.Len)).ToList();
        // one caption line on screen at a time: each line ends when the next begins (never two drawn over each other)
        var lines = new List<(double A, double B, string Text)>();
        foreach (var g in groups)
        {
            if (blocked.Any(b => g[0].Start < b.Item2 && g[^1].End > b.Item1)) continue;
            double end = g[^1].End + 0.25;
            for (int k = 0; k < g.Count; k++)
            {
                double a = g[k].Start, b = k + 1 < g.Count ? g[k + 1].Start : end;
                if (k == 0) a = Math.Max(0, a - 0.05);
                var text = new StringBuilder();
                if (k == 0) text.Append("{\\fad(60,0)}");
                for (int j = 0; j < g.Count; j++)
                {
                    string word = Escape(g[j].Text.ToUpperInvariant());
                    if (j == k)
                    {
                        string color = g[j].Hot ? Hot : Gold;
                        int pop = g[j].Hot ? 124 : 112;
                        text.Append($"{{\\1c{color}\\fscx{pop}\\fscy{pop}\\t(0,110,\\fscx100\\fscy100)}}{word}{{\\1c{White}\\fscx100\\fscy100}}");
                    }
                    else text.Append(word);
                    if (j + 1 < g.Count) text.Append(' ');
                }
                lines.Add((a, b, text.ToString()));
            }
        }
        lines.Sort((x, y) => x.A.CompareTo(y.A));
        for (int i = 0; i < lines.Count; i++)
        {
            double b = i + 1 < lines.Count ? Math.Min(lines[i].B, lines[i + 1].A) : lines[i].B;
            if (b - lines[i].A >= 0.04) Ev(0, lines[i].A, b, "Cap", lines[i].Text);
        }

        // ---- title, chapters, tags, end card
        foreach (var t in plan.Texts)
        {
            double a = t.At, b = t.At + t.Len;
            switch (t.Kind)
            {
                case "title":
                {
                    // the intro, the same every video: your channel name (your logo above it when you've set one) in
                    // gold, a light sweeping across it, two gold rules opening from a diamond, then this video's title
                    string channel = plan.Brand.Channel.Trim();
                    bool logo = plan.Pieces.Any(p => p.Role == "title" && p.Image is not null);
                    string big = channel.Length > 0 ? channel : t.Text, under = channel.Length > 0 ? t.Text : t.Sub ?? "";
                    int cx = W / 2, cy = (int)(H * (logo ? 0.57 : 0.45)), fs = (int)(H * (big.Length > 18 ? 0.075 : 0.1));
                    string tags = $"\\an5\\pos({cx},{cy})\\fs{fs}\\fsp18\\t(0,700,0.6,\\fsp5)\\fscx106\\fscy106\\t(0,550,0.7,\\fscx100\\fscy100)";
                    Ev(2, a, b, "WTitle", $"{{{tags}\\fad(220,320)\\blur6\\t(0,320,\\blur0.6)}}{Escape(big.ToUpperInvariant())}");
                    // (the shine: the same letters in white, seen only through a narrow window moving across)
                    int sw = (int)(W * 0.05);
                    Ev(3, a + 0.35, Math.Min(b, a + 1.4), "WTitle", $"{{{tags}\\1c&HFFFFFF&\\3a&HFF&\\4a&HFF&\\alpha&H70&\\blur2\\clip({cx - W / 3},0,{cx - W / 3 + sw},{H})\\t(0,900,\\clip({cx + W / 3},0,{cx + W / 3 + sw},{H}))}}{Escape(big.ToUpperInvariant())}");
                    int ry = cy + (int)(H * 0.072), rl = (int)(W * 0.17);
                    Ev(1, a + 0.25, b, "Shape", $"{{\\an6\\pos({cx - 14},{ry})\\fad(0,320)\\fscx0\\t(0,500,0.5,\\fscx100)\\p1}}{Rect(rl, 2)}{{\\p0}}");
                    Ev(1, a + 0.25, b, "Shape", $"{{\\an4\\pos({cx + 14},{ry})\\fad(0,320)\\fscx0\\t(0,500,0.5,\\fscx100)\\p1}}{Rect(rl, 2)}{{\\p0}}");
                    Ev(1, a + 0.2, b, "Shape", $"{{\\an7\\pos({cx},{ry + 1})\\fad(150,320)\\frz0\\t(0,500,\\frz180)\\p1}}{Diamond(7)}{{\\p0}}");
                    if (under.Length > 0)
                        Ev(2, a + 0.45, b, "WSub", $"{{\\an5\\pos({cx},{ry + (int)(H * 0.045)})\\fad(260,320)}}{Escape(under.ToUpperInvariant())}");
                    if (channel.Length > 0 && !string.IsNullOrWhiteSpace(t.Sub))
                        Ev(2, a + 0.65, b, "WSmall", $"{{\\an5\\pos({cx},{ry + (int)(H * 0.095)})\\fad(300,320)\\alpha&H50&}}{Escape(t.Sub!.ToUpperInvariant())}");
                    break;
                }
                case "hook":
                {
                    // the hook's line: your words, low on the screen, landing hard
                    Ev(4, a, b, "WQuote", $"{{\\an2\\pos({W / 2},{(int)(H * 0.82)})\\fad(60,180)\\fscx118\\fscy118\\t(0,170,0.6,\\fscx100\\fscy100)}}{Escape(t.Text)}");
                    break;
                }
                case "chapter":
                {
                    // a spell-card banner: night-blue panel, gold frame and corner diamonds, the part's name in gold
                    int x = (int)(W * 0.05), y = (int)(H * 0.70), bw = (int)(W * 0.36), bh = (int)(H * (string.IsNullOrWhiteSpace(t.Sub) ? 0.085 : 0.15)), th = Math.Max(2, H / 360);
                    string mv(int dx, int dy) => $"\\move({x - 50 + dx},{y + dy},{x + dx},{y + dy},0,260)";
                    Ev(1, a, b, "Shape", $"{{\\an7{mv(0, 0)}\\fad(120,260)\\1c{Night}\\1a&H38&\\p1}}{Rect(bw, bh)}{{\\p0}}");
                    Ev(2, a, b, "Shape", $"{{\\an7{mv(0, 0)}\\fad(120,260)\\p1}}{Frame(bw, bh, th)}{{\\p0}}");
                    Ev(2, a + 0.1, b, "Shape", $"{{\\an7{mv(0, 0)}\\fad(120,260)\\p1}}{Diamond(6)}{{\\p0}}");
                    Ev(2, a + 0.1, b, "Shape", $"{{\\an7{mv(0, bh)}\\fad(120,260)\\p1}}{Diamond(6)}{{\\p0}}");
                    Ev(3, a + 0.08, b, "WLabel", $"{{\\an7{mv((int)(W * 0.018), (int)(H * 0.012))}\\fad(160,260)}}{Escape(t.Text)}");
                    if (!string.IsNullOrWhiteSpace(t.Sub))
                        Ev(3, a + 0.2, b, "WQuote", $"{{\\an7{mv((int)(W * 0.018), (int)(H * 0.07))}\\fad(200,260)\\fs{(int)(H * 0.036)}}}{Escape(t.Sub!)}");
                    break;
                }
                case "later":
                {
                    int x = (int)(W * 0.05), y = (int)(H * 0.07);
                    Ev(3, a, b, "WTag", $"{{\\move({x - 140},{y},{x},{y},0,220)\\fad(80,200)}}{Escape(t.Text)}");
                    break;
                }
                case "lapse":
                {
                    // fast-forward: the speed in the top right
                    int x = (int)(W * 0.95), y = (int)(H * 0.07);
                    Ev(3, a, b, "WTag", $"{{\\an9\\pos({x},{y})\\fad(80,120)}}{Escape(t.Text)}");
                    break;
                }
                case "var":
                {
                    // VAR CHECK: a red banner above the replay, then what you said under it
                    int ow = W * 44 / 100, x = W - ow / 2 - 4 - (int)(W * 0.03), y = (int)(H * 0.2) - (int)(H * 0.045);
                    Ev(3, a, b, "Tag", $"{{\\an2\\pos({x},{y})\\fad(120,200)\\3c&H2020D0&\\4c&H2020D0&\\fscx70\\t(0,180,\\fscx100)}}{Escape(t.Text)}");
                    if (!string.IsNullOrWhiteSpace(t.Sub))
                        Ev(3, a + 0.2, b, "Quote", $"{{\\an8\\pos({x},{(int)(H * 0.2) + ow * H / W + 14})\\fad(160,200)\\fs{(int)(H * 0.034)}}}{Escape(t.Sub!)}");
                    break;
                }
                case "end":
                {
                    int cx = W / 2, cy = (int)(H * 0.44);
                    Ev(2, a, b, "WTitle", $"{{\\an5\\pos({cx},{cy})\\fad(250,400)\\fs{(int)(H * 0.075)}\\fsp12\\t(0,700,0.6,\\fsp4)}}{Escape(t.Text)}");
                    int rl = (int)(W * 0.15), ry = cy + (int)(H * 0.06);
                    Ev(1, a + 0.2, b, "Shape", $"{{\\an6\\pos({cx - 14},{ry})\\fad(0,400)\\fscx0\\t(0,500,0.5,\\fscx100)\\p1}}{Rect(rl, 2)}{{\\p0}}");
                    Ev(1, a + 0.2, b, "Shape", $"{{\\an4\\pos({cx + 14},{ry})\\fad(0,400)\\fscx0\\t(0,500,0.5,\\fscx100)\\p1}}{Rect(rl, 2)}{{\\p0}}");
                    if (!string.IsNullOrWhiteSpace(t.Sub)) Ev(2, a + 0.4, b, "WSub", $"{{\\an5\\pos({cx},{ry + (int)(H * 0.05)})\\fad(250,400)}}{Escape(t.Sub!)}");
                    break;
                }
                case "endscreen":
                {
                    // laid out for YouTube's end-screen elements: drag "Video" onto each frame and "Subscribe" onto the ring
                    int cx = W / 2, th = Math.Max(3, H / 270);
                    Ev(2, a, b, "WTitle", $"{{\\an8\\pos({cx},{(int)(H * 0.06)})\\fad(300,500)\\fs{(int)(H * 0.064)}\\fsp12\\t(0,700,0.6,\\fsp4)}}{Escape(t.Text)}");
                    if (!string.IsNullOrWhiteSpace(t.Sub)) Ev(2, a + 0.3, b, "WSmall", $"{{\\an8\\pos({cx},{(int)(H * 0.145)})\\fad(300,500)}}{Escape(t.Sub!)}");
                    int bw = (int)(W * 0.38), bh = bw * 9 / 16, y0 = (int)(H * 0.215);
                    foreach (var (x0, label, delay) in new[] { ((int)(W * 0.075), "PREVIOUS VIDEO", 0.5), (W - (int)(W * 0.075) - bw, "WATCH NEXT", 0.7) })
                    {
                        Ev(1, a + delay, b, "Shape", $"{{\\an7\\pos({x0},{y0})\\fad(300,500)\\1c{Night}\\1a&H50&\\p1}}{Rect(bw, bh)}{{\\p0}}");
                        Ev(2, a + delay, b, "Shape", $"{{\\an7\\pos({x0},{y0})\\fad(300,500)\\p1}}{Frame(bw, bh, th)}{{\\p0}}");
                        foreach (var (dx, dy) in new[] { (0, 0), (bw, 0), (0, bh), (bw, bh) })
                            Ev(2, a + delay + 0.1, b, "Shape", $"{{\\an7\\pos({x0 + dx},{y0 + dy})\\fad(300,500)\\p1}}{Diamond(8)}{{\\p0}}");
                        Ev(2, a + delay + 0.15, b, "WLabel", $"{{\\an8\\pos({x0 + bw / 2},{y0 + bh + (int)(H * 0.022)})\\fad(300,500)\\fs{(int)(H * 0.034)}}}{label}");
                    }
                    int r = (int)(H * 0.078), rcy = (int)(H * 0.80);
                    Ev(1, a + 0.9, b, "Shape", $"{{\\an7\\pos({cx},{rcy})\\fad(300,500)\\1c{Night}\\1a&H50&\\p1}}{Ring(r, r)}{{\\p0}}");
                    Ev(2, a + 0.9, b, "Shape", $"{{\\an7\\pos({cx},{rcy})\\fad(300,500)\\p1}}{Ring(r + th, th + 1)}{{\\p0}}");
                    Ev(2, a + 1.0, b, "WLabel", $"{{\\an8\\pos({cx},{rcy + r + (int)(H * 0.018)})\\fad(300,500)\\fs{(int)(H * 0.03)}}}SUBSCRIBE");
                    break;
                }
            }
        }
        return sb.ToString();
    }
}
