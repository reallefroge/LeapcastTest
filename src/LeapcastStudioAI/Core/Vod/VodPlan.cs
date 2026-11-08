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
    /// <summary>Face pieces: the camera's spot on the picture (crop to it).</summary>
    public LayoutRect? Crop { get; init; }
    /// <summary>Where the piece starts in the finished video.</summary>
    public double At { get; set; }
    public double Length => Hold > 0 ? Hold : (Out - In) / Speed;
}

public sealed record SfxCue(double At, SfxKind Kind, float Gain);
public sealed record MusicSpan(double From, double To, Mood Mood, int Seed, bool TapeStopAtEnd);
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
    /// <summary>The best moment (source seconds): the thumbnail's frame.</summary>
    public double BestMoment { get; set; }
    public ClipLayout? Layout { get; init; }
    public double Duration => Pieces.Count == 0 ? 0 : Pieces[^1].At + FramesOf(Pieces[^1]) / Fps;
    /// <summary>A piece's length in whole frames (every piece is rendered to exactly this many).</summary>
    public int FramesOf(Piece p) => Math.Max(1, (int)Math.Round(p.Length * Fps));
}

public sealed record VodEditOptions(string Title, double TargetSeconds = 900, bool Captions = true, bool Music = true, string Socials = "");
