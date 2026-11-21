using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeapcastStudioAI;

/// <summary>
/// Profiles: complete, independent studio setups (scenes, sources, outputs, hotkeys, settings), one folder each:
/// <c>profiles/&lt;name&gt;/studio-state.json</c> plus an <c>assets</c> folder for images that came with an imported
/// profile. The active profile's name is kept in <c>active-profile.txt</c>.
/// </summary>
public sealed class ProfileStore
{
    public static readonly ProfileStore Default = new(StateStore.Folder);

    private readonly string _root;
    public ProfileStore(string root) => _root = root;

    public string ProfilesFolder => Path.Combine(_root, "profiles");
    private string ActiveFile => Path.Combine(_root, "active-profile.txt");
    private string LegacyState => Path.Combine(_root, "studio-state.json");
    public const string DefaultName = "Default";

    /// <summary>Folder-safe version of a profile name (what's shown stays the user's name).</summary>
    public static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var s = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return s.Length == 0 ? "Profile" : s.Length > 60 ? s[..60] : s;
    }

    public string Folder(string name) => Path.Combine(ProfilesFolder, Safe(name));
    public string StatePath(string name) => Path.Combine(Folder(name), "studio-state.json");
    public string AssetsFolder(string name) => Path.Combine(Folder(name), "assets");

    public string Active
    {
        get
        {
            try
            {
                if (File.Exists(ActiveFile))
                {
                    var n = File.ReadAllText(ActiveFile).Trim();
                    if (n.Length > 0) return n;
                }
            }
            catch { }
            return DefaultName;
        }
    }

    public void SetActive(string name)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ActiveFile, Safe(name));
    }

    public List<string> List()
    {
        var names = Directory.Exists(ProfilesFolder)
            ? Directory.GetDirectories(ProfilesFolder).Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();
        if (!names.Contains(Active, StringComparer.OrdinalIgnoreCase)) names.Add(Active);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Exists(string name) => Directory.Exists(Folder(name));

    /// <summary>Earlier versions kept one state file at the top: it becomes the "Default" profile.</summary>
    public void MigrateLegacy()
    {
        try
        {
            if (!File.Exists(LegacyState) || File.Exists(StatePath(DefaultName))) return;
            Directory.CreateDirectory(Folder(DefaultName));
            File.Copy(LegacyState, StatePath(DefaultName));
            File.Move(LegacyState, LegacyState + ".migrated", overwrite: true);
            if (!File.Exists(ActiveFile)) SetActive(DefaultName);
        }
        catch { /* never block launch */ }
    }

    public void Write(string name, StudioState state)
    {
        Directory.CreateDirectory(Folder(name));
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        string path = StatePath(name), tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public StudioState? Read(string name)
    {
        try
        {
            var p = StatePath(name);
            return File.Exists(p) ? JsonSerializer.Deserialize<StudioState>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    /// <summary>A new profile: a copy of <paramref name="from"/>, or a fresh default setup.</summary>
    public void Create(string name, StudioState? from)
    {
        if (Exists(name)) throw new InvalidOperationException($"A profile called \"{name}\" already exists.");
        var state = from is null ? NewDefaultState() : Clone(from);
        Write(name, state);
        // images that belong to the source profile's own assets are copied along
        if (from is not null) CopyAssetsInto(state, name);
    }

    public void Rename(string oldName, string newName)
    {
        if (Exists(newName)) throw new InvalidOperationException($"A profile called \"{newName}\" already exists.");
        string from = Folder(oldName), to = Folder(newName);
        string oldAssets = AssetsFolder(oldName), newAssets = AssetsFolder(newName);
        Directory.Move(from, to);
        // imported images live inside the profile folder: point the sources at the new place
        var state = Read(newName);
        if (state is not null)
        {
            foreach (var s in state.Scenes.SelectMany(sc => sc.Sources))
                if (s.FilePath.StartsWith(oldAssets, StringComparison.OrdinalIgnoreCase))
                    s.FilePath = newAssets + s.FilePath[oldAssets.Length..];
            Write(newName, state);
        }
        if (string.Equals(Safe(Active), Safe(oldName), StringComparison.OrdinalIgnoreCase)) SetActive(newName);
    }

    public void Delete(string name)
    {
        if (string.Equals(Safe(Active), Safe(name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Switch to another profile before deleting this one.");
        if (Exists(name)) Directory.Delete(Folder(name), recursive: true);
    }

    private void CopyAssetsInto(StudioState state, string name)
    {
        string dest = AssetsFolder(name);
        bool changed = false;
        foreach (var s in state.Scenes.SelectMany(sc => sc.Sources))
        {
            if (string.IsNullOrEmpty(s.FilePath) || !s.FilePath.Contains(Path.DirectorySeparatorChar + "profiles" + Path.DirectorySeparatorChar)
                || !File.Exists(s.FilePath)) continue;
            Directory.CreateDirectory(dest);
            string target = Path.Combine(dest, Path.GetFileName(s.FilePath));
            if (!File.Exists(target)) File.Copy(s.FilePath, target);
            s.FilePath = target;
            changed = true;
        }
        if (changed) Write(name, state);
    }

    public static StudioState Clone(StudioState s) => JsonSerializer.Deserialize<StudioState>(JsonSerializer.Serialize(s))!;

    public static StudioState NewDefaultState()
    {
        var initial = new StudioState { Version = StudioState.CurrentVersion };   // new, so nothing to migrate
        var scene = new StudioScene { Name = "Wizard101" };
        scene.Sources.Add(new StudioSource { Name = "Wizard101 (screen)", Type = SourceTypes.Display });
        scene.Sources.Add(new StudioSource { Name = "Desktop audio", Type = SourceTypes.DesktopAudio, NoiseCleanup = false });
        scene.Sources.Add(new StudioSource { Name = "Microphone", Type = SourceTypes.Microphone });
        initial.Scenes.Add(scene);
        initial.SelectedSceneId = scene.Id;
        return initial;
    }
}

/// <summary>Wrong or missing password for the stream keys in a profile file.</summary>
public sealed class ProfilePasswordException : Exception
{
    public ProfilePasswordException(string message) : base(message) { }
}

/// <summary>
/// One-file profile export (<c>.leapcastprofile</c>, a zip) for moving a complete setup to a fresh install:
/// <list type="bullet">
/// <item><c>profile.json</c>: scenes, sources (positions, crops, filters, hotkeys), outputs, studio hotkeys, settings;</item>
/// <item><c>assets/</c>: every image the scenes use, so logos and BRB cards come along;</item>
/// <item><c>keys.json</c> (optional): stream keys encrypted with a password (PBKDF2-SHA256 + AES-GCM). Without a
/// password, keys are left out: stream keys are never written in plain text, and Windows' own key protection only
/// works on the PC that created it.</item>
/// </list>
/// Settings that only make sense on one PC (the FFmpeg path, a recordings folder that doesn't exist there) are reset.
/// </summary>
public static class ProfilePackage
{
    public const string Extension = ".leapcastprofile";
    private const int Format = 1, Iterations = 200_000;

    public sealed record Info(string Name, bool HasKeys, int Scenes, int Sources, string AppVersion);

    public static void Export(StudioState state, string profileName, string zipPath, Func<string, string> unprotect, string? password, string appVersion = "")
    {
        var copy = ProfileStore.Clone(state);
        copy.Settings.FfmpegPath = "";
        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            // images used by the scenes
            var assetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in copy.Scenes.SelectMany(sc => sc.Sources))
            {
                if (string.IsNullOrWhiteSpace(s.FilePath) || !File.Exists(s.FilePath)) continue;
                if (!assetNames.TryGetValue(s.FilePath, out var entry))
                {
                    entry = $"assets/{assetNames.Count:D3}_{Path.GetFileName(s.FilePath)}";
                    zip.CreateEntryFromFile(s.FilePath, entry);
                    assetNames[s.FilePath] = entry;
                }
                s.FilePath = "@" + entry;
            }
            // stream keys: encrypted with the password, or left out
            int keys = 0;
            if (!string.IsNullOrEmpty(password))
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
                var map = new JsonObject();
                foreach (var o in copy.Settings.Outputs)
                {
                    string plain = unprotect(o.StreamKeyProtected);
                    if (plain.Length == 0) continue;
                    map[o.Id.ToString("N")] = Seal(key, plain);
                    keys++;
                }
                if (keys > 0)
                {
                    var doc = new JsonObject { ["format"] = Format, ["kdf"] = "pbkdf2-sha256", ["iterations"] = Iterations,
                        ["salt"] = Convert.ToBase64String(salt), ["check"] = Seal(key, "leapcast"), ["keys"] = map };
                    WriteEntry(zip, "keys.json", doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            foreach (var o in copy.Settings.Outputs) o.StreamKeyProtected = "";
            copy.Settings.StreamKeyProtected = "";
            WriteEntry(zip, "profile.json", JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
            var manifest = new JsonObject
            {
                ["format"] = Format, ["name"] = profileName, ["app"] = appVersion, ["created"] = DateTime.UtcNow.ToString("o"),
                ["hasKeys"] = keys > 0, ["scenes"] = copy.Scenes.Count, ["sources"] = copy.Scenes.Sum(sc => sc.Sources.Count),
            };
            WriteEntry(zip, "manifest.json", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        File.WriteAllBytes(zipPath, mem.ToArray());
    }

    public static Info Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var m = JsonNode.Parse(ReadEntry(zip, "manifest.json") ?? throw new InvalidDataException("This isn't a Leapcast profile file."))!;
        if ((int?)m["format"] is not int f || f > Format) throw new InvalidDataException("This profile was made by a newer version of Leapcast. Update Leapcast first.");
        return new Info((string?)m["name"] ?? "Imported", (bool?)m["hasKeys"] ?? false, (int?)m["scenes"] ?? 0, (int?)m["sources"] ?? 0, (string?)m["app"] ?? "");
    }

    /// <summary>
    /// Reads a profile file into a state ready to use on this PC: images are extracted into
    /// <paramref name="assetsFolder"/>, stream keys decrypted with <paramref name="password"/> and re-protected with
    /// <paramref name="protect"/> (skipped when no password is given).
    /// </summary>
    public static StudioState Import(string zipPath, string assetsFolder, Func<string, string> protect, string? password)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        Inspect(zipPath);
        var state = JsonSerializer.Deserialize<StudioState>(ReadEntry(zip, "profile.json") ?? throw new InvalidDataException("The profile file is damaged."))
                    ?? throw new InvalidDataException("The profile file is damaged.");
        foreach (var s in state.Scenes.SelectMany(sc => sc.Sources))
        {
            if (!s.FilePath.StartsWith('@')) continue;
            var entry = zip.GetEntry(s.FilePath[1..]);
            if (entry is null) { s.FilePath = ""; continue; }
            Directory.CreateDirectory(assetsFolder);
            string target = Path.Combine(assetsFolder, Path.GetFileName(entry.FullName));
            entry.ExtractToFile(target, overwrite: true);
            s.FilePath = target;
        }
        if (ReadEntry(zip, "keys.json") is { } keysJson && !string.IsNullOrEmpty(password))
        {
            var doc = JsonNode.Parse(keysJson)!;
            var salt = Convert.FromBase64String((string)doc["salt"]!);
            int iterations = (int?)doc["iterations"] ?? Iterations;
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
            if (Open(key, (string)doc["check"]!) != "leapcast") throw new ProfilePasswordException("That password doesn't match the one the profile was exported with.");
            var map = doc["keys"]!.AsObject();
            foreach (var o in state.Settings.Outputs)
                if (map[o.Id.ToString("N")] is JsonNode n && Open(key, (string)n!) is { } plain)
                    o.StreamKeyProtected = protect(plain);
        }
        // settings that only made sense on the PC it came from
        state.Settings.FfmpegPath = "";
        if (!Directory.Exists(state.Settings.RecordingFolder)) state.Settings.RecordingFolder = new StudioSettings().RecordingFolder;
        if (state.Scenes.Count == 0) state = ProfileStore.NewDefaultState();
        state.SelectedSceneId ??= state.Scenes[0].Id;
        return state;
    }

    private static string Seal(byte[] key, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private static string? Open(byte[] key, string sealedText)
    {
        try
        {
            var all = Convert.FromBase64String(sealedText);
            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }
}

/// <summary>Studio-wide hotkey actions (Settings → Hotkeys).</summary>
public static class HotkeyActions
{
    public const string GoLive = "GoLive", Record = "Record", SaveReplay = "SaveReplay", Preview = "Preview",
        Transition = "Transition", StudioMode = "StudioMode";

    public static readonly (string Id, string Label)[] All =
    {
        (GoLive, "Go live / end stream"),
        (Record, "Start / stop recording"),
        (SaveReplay, "Save replay"),
        (Preview, "Start / stop preview"),
        (StudioMode, "Studio mode on / off"),
        (Transition, "Transition (studio mode)"),
    };

    /// <summary>Combinations used by more than one action, scene or source (only the first would work).</summary>
    public static List<string> Conflicts(StudioSettings settings, IEnumerable<StudioScene> scenes)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? hk, string owner)
        {
            var n = HotkeyText.Normalize(hk);
            if (n.Length == 0) return;
            if (!owners.TryGetValue(n, out var set)) owners[n] = set = new HashSet<string>();
            set.Add(owner);
        }
        foreach (var (id, hk) in settings.Hotkeys) Add(hk, "action:" + id);
        foreach (var sc in scenes)
        {
            Add(sc.Hotkey, "scene:" + sc.Id);
            // copies of one source (same name and type) sharing a hotkey are meant to switch together
            foreach (var s in sc.Sources) Add(s.Hotkey, $"source:{s.Name}|{s.Type}");
        }
        return owners.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).OrderBy(k => k).ToList();
    }
}
