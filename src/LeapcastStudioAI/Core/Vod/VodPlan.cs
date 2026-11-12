namespace LeapcastStudioAI.Vod;

/// <summary>A punch-in during a piece (piece-local seconds): eases in, holds, eases out.</summary>
public sealed record Zoom(double At, double Len, double Scale);

/// <summary>One continuous stretch of the recording in the edit, with what happens to it.</summary>
public sealed class Piece
{
    public required string Role { get; init; }  // cold, title, moment, slow, face, end
    public required double In { get; init; }    // source seconds
    public required double Out { get; init; }
    public double Speed { get; init; } = 1;     // 0.5 = half-speed slow motion
    public double Hold { get; init; }           // end card: freeze the last frame this long
    public List<Zoom> Zooms { get; } = new();
    /// <summary>A slow, continuous push in (+) or pull out (−) across the piece: keeps a long shot alive.</summary>
    public double Push { get; set; }
    public bool WhipIn { get; set; }
    public bool WhipOut { get; set; }
    public bool Shake { get; set; }
    public bool Flash { get; set; }             // a red flash on the first frames (a death)
    public bool Letterbox { get; set; }
    public bool Blur { get; set; }
    public bool Mute { get; set; }
    /// <summary>Level change for this piece's sound (dB): evens out loud and quiet parts.</summary>
    public double GainDb { get; set; }
    /// <summary>The transition into / out of this piece (<see cref="TransitionKinds"/>; null = a plain cut).</summary>
    public string? TransIn { get; set; }
    public string? TransOut { get; set; }
    /// <summary>Another file this piece comes from (your intro clip), instead of the recording.</summary>
    public string? Source { get; init; }
    /// <summary>A picture laid over the middle of the piece (your logo in the intro).</summary>
    public string? Image { get; init; }
    /// <summary>Darkened under text (the intro).</summary>
    public bool Darken { get; init; }
    /// <summary>A replay laid over this piece (the VAR check: what you did, while you say it).</summary>
    public PieceOverlay? Overlay { get; set; }
    /// <summary>Face pieces: the camera's spot on the picture (crop to it).</summary>
    public LayoutRect? Crop { get; init; }
    /// <summary>Where the piece starts in the finished video.</summary>
    public double At { get; set; }
    public double Length => Hold > 0 ? Hold : (Out - In) / Speed;
}

/// <param name="Seed">A fixed seed: the same sound every video (the intro's sting); 0 = varied.</param>
public sealed record SfxCue(double At, SfxKind Kind, float Gain, int Seed = 0);

/// <summary>Footage played over a piece: source In–Out, sped up to fit Length seconds, starting Start seconds into the piece.</summary>
public sealed record PieceOverlay(double In, double Out, double Start, double Length, string Quote);

/// <summary>A cut between two clips of the edit: what the editor suggests there, and what it uses.</summary>
/// <param name="From">Where the incoming clip starts in the recording (stays put when the edit is re-planned).</param>
public sealed record CutInfo(double At, double From, string Style, string Sound, string Suggested, string SuggestedSound, string Why, IReadOnlyList<string> Options);

/// <summary>The transitions the editor can put between clips, and the sounds that can go with them.</summary>
public static class TransitionKinds
{
    public const string None = "none", Whip = "whip", Fade = "fade", Flash = "flash", Blur = "blur", Zoom = "zoom", Glitch = "glitch", Timelapse = "timelapse", Mixed = "mixed";
    public static readonly string[] Styles = { None, Whip, Fade, Flash, Blur, Zoom, Glitch, Timelapse };
    public static readonly string[] Sounds = { "none", "whoosh", "soft", "pop", "impact", "riser" };
    public static string Name(string style) => style switch
    {
        None => "Cut", Whip => "Whip pan", Fade => "Fade through black", Flash => "Flash to white", Blur => "Blur", Zoom => "Zoom punch",
        Glitch => "Glitch", Timelapse => "Timelapse (fast-forward the time between)", Mixed => "Varied (the editor picks per cut)", _ => style,
    };
    public static string SoundName(string sound) => sound switch
    {
        "none" => "No sound", "whoosh" => "Whoosh", "soft" => "Soft whoosh", "pop" => "Pop", "impact" => "Impact", "riser" => "Riser",
        "auto" => "Matches the transition", Mixed => "Varied", _ => sound,
    };
    /// <summary>The sound that suits a transition.</summary>
    public static string SoundFor(string style) => style switch
    {
        Whip => "whoosh", Fade => "soft", Flash => "impact", Blur => "soft", Zoom => "riser", Glitch => "pop", Timelapse => "soft", _ => "none",
    };
    public static SfxKind? Kind(string sound) => sound switch
    {
        "whoosh" => SfxKind.Whoosh, "soft" => SfxKind.SoftWhoosh, "pop" => SfxKind.Pop, "impact" => SfxKind.Impact, "riser" => SfxKind.Riser, _ => null,
    };
}
public sealed record MusicSpan(double From, double To, Mood Mood, int Seed, bool TapeStopAtEnd, MusicStyle Style = MusicStyle.Edm);
/// <summary>On-screen text: title, chapter, later, end (timeline seconds).</summary>
public sealed record TextCue(string Kind, double At, double Len, string Text, string? Sub = null);
/// <summary>A caption word placed on the finished video's timeline.</summary>
public sealed record CaptionCue(string Text, double Start, double End, bool Hot);

public sealed class EditPlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Fps { get; init; }
    public required string Title { get; init; }
    public List<Piece> Pieces { get; } = new();
    public List<SfxCue> Sfx { get; } = new();
    public List<MusicSpan> Music { get; } = new();
    public List<TextCue> Texts { get; } = new();
    public List<CaptionCue> Captions { get; } = new();
    public List<(double At, string Title)> Chapters { get; } = new();
    /// <summary>The stretches of the recording in the edit (for the review panel).</summary>
    public List<EditMoment> Moments { get; } = new();
    /// <summary>Music and sound-effect levels (1 = as made).</summary>
    public double MusicVolume { get; set; } = 1;
    public double SfxVolume { get; set; } = 1;
    /// <summary>Your channel's look (intro, music, end screen).</summary>
    public VodBrand Brand { get; set; } = new();
    /// <summary>The cuts between clips (for the review panel), in order.</summary>
    public List<CutInfo> Cuts { get; } = new();
    /// <summary>Parts brought to one loudness (each piece's <see cref="Piece.GainDb"/>, plus smoothing in the mix).</summary>
    public bool EvenLoudness { get; set; }
    /// <summary>The best moment (source seconds): the thumbnail's frame.</summary>
    public double BestMoment { get; set; }
    public ClipLayout? Layout { get; init; }
    public double Duration => Pieces.Count == 0 ? 0 : Pieces[^1].At + FramesOf(Pieces[^1]) / Fps;
    /// <summary>A piece's length in whole frames (every piece is rendered to exactly this many).</summary>
    public int FramesOf(Piece p) => Math.Max(1, (int)Math.Round(p.Length * Fps));

    /// <summary>The same edit at another size (the quick preview: everything placed the same, text scaled with it).</summary>
    public EditPlan Resized(int width, int height, double fps)
    {
        var p = new EditPlan { Width = width, Height = height, Fps = fps, Title = Title, Layout = Layout };
        p.Pieces.AddRange(Pieces); p.Sfx.AddRange(Sfx); p.Music.AddRange(Music); p.Texts.AddRange(Texts); p.Captions.AddRange(Captions);
        p.Chapters.AddRange(Chapters); p.Moments.AddRange(Moments); p.Cuts.AddRange(Cuts);
        p.MusicVolume = MusicVolume; p.SfxVolume = SfxVolume; p.Brand = Brand; p.EvenLoudness = EvenLoudness; p.BestMoment = BestMoment;
        return p;
    }
}

public sealed record VodEditOptions(string Title, double TargetSeconds = 900, bool Captions = true, bool Music = true, string Socials = "", bool Chapters = true,
    VodBrand? Brand = null);

/// <summary>
/// Your channel's look, the same on every video (so the intro becomes recognisable): the channel name, your logo,
/// your music (a folder of tracks, e.g. a game's soundtrack you have; one intro track so the intro always sounds the
/// same), an intro clip of your own footage, a hook up front and an end screen laid out for YouTube's end-screen
/// elements. The look is Wizard101-styled (gold, deep blue, a fantasy serif) whatever game the video is.
/// </summary>
public sealed record VodBrand(string Channel = "", string? Logo = null, string? MusicFolder = null, string? IntroMusic = null, string? IntroClip = null,
    string MusicSource = "generated", bool EndScreen = true, bool Hook = true);
