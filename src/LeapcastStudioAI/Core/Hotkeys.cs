namespace LeapcastStudioAI;

/// <summary>Sources already set up somewhere, offered by "+" so they don't have to be set up again.</summary>
public static class SourceLibrary
{
    /// <summary>Sources in other scenes, one per name and type (the same camera in three scenes is offered once),
    /// minus what <paramref name="target"/> already has.</summary>
    public static List<(StudioSource Source, string Scene)> FromOtherScenes(IEnumerable<StudioScene> scenes, StudioScene? target)
    {
        var here = target?.Sources.Select(s => (s.Name, s.Type)).ToHashSet() ?? new();
        var seen = new HashSet<(string, string)>();
        var list = new List<(StudioSource, string)>();
        foreach (var sc in scenes)
        {
            if (ReferenceEquals(sc, target)) continue;
            foreach (var s in sc.Sources)
                if (!here.Contains((s.Name, s.Type)) && seen.Add((s.Name, s.Type))) list.Add((s, sc.Name));
        }
        return list;
    }

    /// <summary>
    /// Every source of this type you already have, in any scene (<paramref name="target"/> included), one per name
    /// (copies of one camera in three scenes are offered once), with the scenes it's in. The one in
    /// <paramref name="target"/> is the one copied when there is one.
    /// </summary>
    public static List<(StudioSource Source, List<string> Scenes)> Existing(IEnumerable<StudioScene> scenes, string type, StudioScene? target = null)
    {
        var all = scenes.ToList();
        if (target is not null && !all.Contains(target)) all.Insert(0, target);
        var byName = new Dictionary<string, (StudioSource Source, List<string> Scenes)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var sc in all.OrderBy(sc => ReferenceEquals(sc, target) ? 0 : 1))
            foreach (var s in sc.Sources.Where(x => x.Type == type))
            {
                if (!byName.TryGetValue(s.Name, out var e)) { byName[s.Name] = e = (s, new List<string>()); order.Add(s.Name); }
                if (!e.Scenes.Contains(sc.Name)) e.Scenes.Add(sc.Name);
            }
        return order.Select(n => byName[n]).ToList();
    }
}

/// <summary>
/// Hotkey text like "Ctrl+Shift+F1": modifiers first (Ctrl, Alt, Shift, Win), then one key name. Kept as text in the
/// scene file so it's readable and survives across PCs; the Windows side turns it into a system-wide hotkey.
/// </summary>
public static class HotkeyText
{
    [Flags] public enum Mods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 } // same bits as Win32 MOD_*

    public static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Win" };

    /// <summary>Normalised text, or null when it's not a usable hotkey (no key, or only modifiers).</summary>
    public static (Mods Mods, string Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = Mods.None;
        string? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Mods.Ctrl; break;
                case "alt": mods |= Mods.Alt; break;
                case "shift": mods |= Mods.Shift; break;
                case "win": case "windows": mods |= Mods.Win; break;
                default:
                    if (key is not null) return null; // two keys
                    key = p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..];
                    break;
            }
        }
        return key is null ? null : (mods, key);
    }

    public static string Format(Mods mods, string key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Mods.Win)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>The same hotkey written differently ("shift+ctrl+f1" vs "Ctrl+Shift+F1") compares equal.</summary>
    public static string Normalize(string? text) => Parse(text) is { } h ? Format(h.Mods, h.Key) : "";

    /// <summary>
    /// Every hotkey in the scene collection and the sources it toggles (copies of a source in several scenes
    /// share a hotkey and toggle together).
    /// </summary>
    public static Dictionary<string, List<StudioSource>> Map(IEnumerable<StudioScene> scenes)
    {
        var map = new Dictionary<string, List<StudioSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scenes.SelectMany(sc => sc.Sources))
        {
            string k = Normalize(s.Hotkey);
            if (k.Length == 0) continue;
            if (!map.TryGetValue(k, out var list)) map[k] = list = new List<StudioSource>();
            list.Add(s);
        }
        return map;
    }

    /// <summary>Toggles a group: if any is shown, hide them all; otherwise show them all. Returns the new state.</summary>
    public static bool Toggle(IReadOnlyList<StudioSource> group)
    {
        bool show = !group.Any(s => s.Visible);
        foreach (var s in group) s.Visible = show;
        return show;
    }
}
