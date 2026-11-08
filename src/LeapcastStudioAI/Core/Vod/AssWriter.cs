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

    private static string T(double s)
    {
        if (s < 0) s = 0;
        int cs = (int)Math.Round(s * 100);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
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
        var blocked = plan.Texts.Where(t => t.Kind is "title" or "end" or "chapter").Select(t => (t.At - 0.1, t.At + t.Len)).ToList();
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
                        string color = g[j].Hot ? Hot : Yellow;
                        int pop = g[j].Hot ? 124 : 112;
                        text.Append($"{{\\1c{color}\\fscx{pop}\\fscy{pop}\\t(0,110,\\fscx100\\fscy100)}}{word}{{\\1c{White}\\fscx100\\fscy100}}");
                    }
                    else text.Append(word);
                    if (j + 1 < g.Count) text.Append(' ');
                }
                Ev(0, a, b, "Cap", text.ToString());
            }
        }

        // ---- title, chapters, tags, end card
        foreach (var t in plan.Texts)
        {
            double a = t.At, b = t.At + t.Len;
            switch (t.Kind)
            {
                case "title":
                {
                    int cx = W / 2, cy = (int)(H * 0.46);
                    Ev(2, a, b, "Title", $"{{\\pos({cx},{cy})\\fad(120,260)\\blur10\\t(0,200,\\blur0)\\fscx132\\fscy132\\t(0,280,0.6,\\fscx100\\fscy100)}}{Escape(t.Text.ToUpperInvariant())}");
                    int barW = (int)(W * 0.16);
                    Ev(1, a + 0.12, b, "Bar", $"{{\\an5\\pos({cx},{cy + (int)(H * 0.085)})\\fad(0,260)\\fscx0\\t(0,320,0.5,\\fscx100)\\p1}}m 0 0 l {barW} 0 {barW} 7 0 7{{\\p0}}");
                    if (!string.IsNullOrWhiteSpace(t.Sub))
                        Ev(2, a + 0.25, b, "Sub", $"{{\\pos({cx},{cy + (int)(H * 0.13)})\\fad(220,260)}}{Escape(t.Sub!.ToUpperInvariant())}");
                    break;
                }
                case "chapter":
                {
                    int x = (int)(W * 0.065), y = (int)(H * 0.72);
                    Ev(1, a, b, "Bar", $"{{\\an7\\pos({x - 24},{y - (int)(H * 0.03)})\\fad(80,240)\\fscy0\\t(0,220,\\fscy100)\\p1}}m 0 0 l 8 0 8 {(int)(H * 0.13)} 0 {(int)(H * 0.13)}{{\\p0}}");
                    Ev(2, a + 0.05, b, "Label", $"{{\\move({x - 40},{y},{x},{y},0,240)\\fad(140,240)}}{Escape(t.Text)}");
                    if (!string.IsNullOrWhiteSpace(t.Sub))
                        Ev(2, a + 0.18, b, "Quote", $"{{\\move({x - 60},{y + (int)(H * 0.058)},{x},{y + (int)(H * 0.058)},0,300)\\fad(180,240)}}{Escape(t.Sub!)}");
                    break;
                }
                case "later":
                {
                    int x = (int)(W * 0.05), y = (int)(H * 0.07);
                    Ev(3, a, b, "Tag", $"{{\\move({x - 140},{y},{x},{y},0,220)\\fad(80,200)}}{Escape(t.Text)}");
                    break;
                }
                case "end":
                {
                    int x = (int)(W * 0.08), y = (int)(H * 0.42);
                    Ev(2, a, b, "Title", $"{{\\an4\\pos({x},{y})\\fad(200,400)\\fscx118\\fscy118\\t(0,300,0.6,\\fscx100\\fscy100)\\fs{(int)(H * 0.075)}}}{Escape(t.Text)}");
                    Ev(1, a + 0.15, b, "Bar", $"{{\\an7\\pos({x},{y + (int)(H * 0.06)})\\fad(0,400)\\fscx0\\t(0,360,0.5,\\fscx100)\\p1}}m 0 0 l {(int)(W * 0.22)} 0 {(int)(W * 0.22)} 7 0 7{{\\p0}}");
                    if (!string.IsNullOrWhiteSpace(t.Sub))
                        Ev(2, a + 0.35, b, "Sub", $"{{\\an4\\pos({x},{y + (int)(H * 0.12)})\\fad(250,400)}}{Escape(t.Sub!)}");
                    Ev(2, a + 0.55, b, "Sub", $"{{\\an4\\pos({x},{y + (int)(H * (string.IsNullOrWhiteSpace(t.Sub) ? 0.12 : 0.18))})\\fad(250,400)\\alpha&H60&}}SEE YOU NEXT STREAM");
                    break;
                }
            }
        }
        return sb.ToString();
    }
}
