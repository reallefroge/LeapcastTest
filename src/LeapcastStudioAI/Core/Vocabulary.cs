using System.Text.RegularExpressions;
using LeapcastStudioAI.Vod;

namespace LeapcastStudioAI;

/// <summary>
/// Names captions must spell the game's way: Whisper is told them up front (its prompt), and anything a recognizer
/// still writes another way ("Chrysalis", "dragon spire", "Mushu") is put right afterwards, joining words when a
/// name was heard as two or three. Wizard101's worlds and names are built in; your own come from Settings
/// ("Khrysalis = Chrysalis, Crysalis", one per line; a name alone just fixes its capital letters).
/// </summary>
public static class Vocabulary
{
    private static readonly string[] BuiltIn =
    {
        "Wizard101 = wizard 101, wizard one oh one, wizard one-oh-one, wizard 1 0 1",
        "Khrysalis = chrysalis, crysalis, krysalis, chrysallis, crisalis, chrysalus, chrysales, khrysallis, kris alice, chris alice",
        "Khrys = chrys, crys, krys",
        "Krokotopia = crocotopia, krokatopia, crockotopia, crocodopia, crokotopia, krocotopia, croc o topia, croco topia, kroko topia, croc a topia",
        "Marleybone = marlybone, marley bone, marly bone, marleybowne",
        "MooShu = mushu, mooshoo, mooshu, moo shu, moo shoo, mu shu",
        "Dragonspyre = dragonspire, dragon spire, dragon spyre, dragon's spire",
        "Zafaria = safaria, zafarria, zaffaria, zafaria",
        "Azteca = asteca, azteka, aztecca",
        "Empyrea = empyria, empiria, empyrea, empirea",
        "Karamelle = karamel, caramelle, karamell",
        "Lemuria = lamuria, lemurya, lemuria",
        "Wallaru = walaru, wallaroo, wall aroo, wala roo",
        "Selenopolis = selenapolis, seleno polis, selena polis, selenopolis",
        "Grizzleheim = grizzlehime, grizzleham, grizzlheim, grizzle heim, grizzly heim, grizzle home",
        "Wysteria = wisteria",
        "Darkmoor = dark moor, darkmore, dark more",
        "Ravenwood = raven wood",
        "Malistaire = malistair, mallistair, malastair, malistare, malice stare, mallistaire",
        "Morganthe = morgantha, morganth, morganthe",
        "Avalon, Polaris, Mirage, Novus, Aquila, Azteca, Celestia, Krokotopia",
        "Pyromancer, Thaumaturge, Diviner, Necromancer, Theurgist, Conjurer, Sorcerer",
    };

    private static volatile Table _table = Build("");

    /// <summary>Your own spellings (Settings → Recording → "Names to spell right").</summary>
    public static void SetUser(string? lines) => _table = Build(lines ?? "");

    /// <summary>What Whisper is told first, so it writes these names the right way (kept short: Whisper reads ~200 tokens).</summary>
    public static string Prompt()
    {
        // yours first (Whisper pays most attention to the start), then Wizard101's
        var names = _table.UserNames.Concat(_table.Names.Except(_table.UserNames)).ToList();
        var s = "Wizard101 stream, questing in Khrysalis. " + string.Join(", ", names.Take(40)) + ".";
        return s.Length > 600 ? s[..600] : s;
    }

    /// <summary>The proper name this word is (a Wizard101 place or one of yours), or null.</summary>
    public static string? NameOf(string word)
    {
        var t = _table;
        string k = Norm(word.Trim().TrimEnd('.', ',', '!', '?'));
        return k.Length > 0 && t.Map.TryGetValue(k, out var v) && t.Names.Contains(v) ? v : null;
    }

    public static List<CaptionWord> Fix(IReadOnlyList<CaptionWord> words) =>
        Fix(words, w => w.Text, (first, last, text) => new CaptionWord(text, first.StartUtc, last.EndUtc));

    public static List<VodWord> Fix(IReadOnlyList<VodWord> words) =>
        Fix(words, w => w.Text, (first, last, text) => new VodWord(text, first.Start, last.End));

    /// <summary>Fixes one piece of text (titles, lines): every known misspelling becomes the right name.</summary>
    public static string FixText(string text)
    {
        var parts = Regex.Split(text.Trim(), @"\s+").Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return text;
        return string.Join(" ", Fix(parts, p => p, (a, b, t) => t));
    }

    private static List<T> Fix<T>(IReadOnlyList<T> words, Func<T, string> text, Func<T, T, string, T> make)
    {
        var t = _table;
        var o = new List<T>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            bool done = false;
            for (int n = Math.Min(t.MaxWords, words.Count - i); n >= 1 && !done; n--)
            {
                var (key, tail) = Key(words.Skip(i).Take(n).Select(text), n);
                if (key.Length == 0) continue;
                if (!t.Map.TryGetValue(key, out var right)) continue;
                string lead = Regex.Match(text(words[i]), @"^[^\w]*").Value;
                o.Add(make(words[i], words[i + n - 1], lead + right + tail));
                i += n - 1;
                done = true;
            }
            if (!done) o.Add(words[i]);
        }
        // a name heard as an ordinary word only where the ordinary word makes no sense: "in Christmas" is Khrysalis
        // (people say "for/at/on Christmas"), and so on
        for (int i = 1; i < o.Count; i++)
            foreach (var (heard, name, after) in Context)
            {
                var (key, tail) = Key(new[] { text(o[i]) }, 1);
                if (key != heard) continue;
                string p1 = Norm(text(o[i - 1])), p2 = i >= 2 ? Norm(text(o[i - 2])) + " " + p1 : "";
                if (!after.Contains(p1) && !after.Contains(p2)) continue;
                string lead = Regex.Match(text(o[i]), @"^[^\w]*").Value;
                o[i] = make(o[i], o[i], lead + name + tail);
            }
        return o;
    }

    private static readonly (string Heard, string Name, HashSet<string> After)[] Context =
    {
        ("christmas", "Khrysalis", new HashSet<string> { "in", "into", "through", "from", "back to", "go to", "going to", "get to", "got to",
            "went to", "head to", "heading to", "off to", "over to", "teleport to", "in the", "of" }),
        ("christmas's", "Khrysalis's", new HashSet<string> { "in", "into", "through", "from" }),
    };

    /// <summary>The lookup key for a run of words (lowercase, punctuation out) and what to put back after the name ("'s", "!").</summary>
    private static (string Key, string Tail) Key(IEnumerable<string> parts, int n)
    {
        var list = parts.ToList();
        string last = list[^1];
        var m = Regex.Match(last, @"('s|s')?[^\w']*$");
        string tail = m.Value;
        if (tail.Length > 0) list[^1] = last[..^tail.Length];
        string key = string.Join(" ", list.Select(p => Regex.Replace(p.ToLowerInvariant(), @"[^\w' -]", "").Trim('\'')));
        key = Regex.Replace(key, @"\s+", " ").Trim();
        return (key, tail);
    }

    private sealed record Table(Dictionary<string, string> Map, List<string> Names, int MaxWords, List<string> UserNames);

    private static Table Build(string user)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        void Add(string line, bool mine)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) return;
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                // a list of names: their capitals (and, from Settings, Whisper's prompt)
                foreach (var name in line.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    map[Norm(name)] = name;
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
                return;
            }
            string right = line[..eq].Trim();
            if (right.Length == 0) return;
            if (!names.Contains(right, StringComparer.OrdinalIgnoreCase)) names.Add(right);
            map[Norm(right)] = right;
            foreach (var v in line[(eq + 1)..].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                if (mine || !map.ContainsKey(Norm(v)) || map[Norm(v)] == right) map[Norm(v)] = right;
        }
        foreach (var l in BuiltIn) Add(l, false);
        // yours come last, so they win
        foreach (var l in user.Split('\n')) Add(l, true);
        var userNames = user.Split('\n').SelectMany(l => (l.Contains('=') ? l[..l.IndexOf('=')] : l).Split(','))
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int max = map.Keys.Select(k => k.Count(c => c == ' ') + 1).DefaultIfEmpty(1).Max();
        return new Table(map, names, Math.Min(4, max), userNames);
    }

    private static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\w' -]", ""), @"\s+", " ").Trim();
}
