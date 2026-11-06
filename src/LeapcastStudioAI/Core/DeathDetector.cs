namespace LeapcastStudioAI;

/// <summary>
/// Spots a Wizard101 death on the program picture: the big red health globe at the bottom-left of the game window
/// drains to an empty pale-gray globe with a yellow "0". The globe is found while it's red (the reddest blob in the
/// bottom-left corner of the game), then watched a few times a second. A loading screen hides the HUD instead (no
/// gray disc with a yellow digit), so it doesn't count.
/// The fight starts when the HP starts changing after a calm stretch; the clip runs from just before that to just
/// after the death.
/// </summary>
public sealed class DeathDetector
{
    /// <summary>A death: when the fight began and when the HP hit 0 (UTC).</summary>
    public event Action<DateTime, DateTime>? Died;
    public event Action<string>? Debug;

    public static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxFight = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BeforeFight = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CalmFor = TimeSpan.FromSeconds(12);

    private DateTime _lastSample = DateTime.MinValue;
    // the globe, as a fraction of the game rectangle (found while red)
    private (double X, double Y, double W, double H)? _globe;
    private DateTime _lastAlive = DateTime.MinValue;
    private int _emptyStreak;
    private bool _armed = true;          // a death fires once, then waits for HP to come back
    private double _calmLevel = -1;      // HP fill during the current calm stretch
    private DateTime _calmSince = DateTime.MinValue, _fightStart = DateTime.MinValue;

    public double LastFill { get; private set; }
    public bool GlobeFound => _globe is not null;

    /// <summary>
    /// One program frame (BGRA). <paramref name="game"/>: where the game window is drawn in this frame, in pixels.
    /// Cheap: only the bottom-left corner of the game is read, every other pixel, a few times a second.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> bgra, int frameW, int frameH, LayoutRect game, DateTime now)
    {
        if (now - _lastSample < SampleEvery) return;
        _lastSample = now;
        if (game.W < 64 || game.H < 64) return;

        // ---- find the globe: the red blob in the bottom-left corner (x 0–25%, y 65–100% of the game) ----
        int x0 = (int)Math.Max(0, game.X), x1 = (int)Math.Min(frameW - 1, game.X + game.W * 0.25);
        int y0 = (int)Math.Max(0, game.Y + game.H * 0.65), y1 = (int)Math.Min(frameH - 1, game.Y + game.H - 1);
        var xs = new List<int>(); var ys = new List<int>();
        for (int y = y0; y <= y1; y += 2)
            for (int x = x0; x <= x1; x += 2)
                if (Class(bgra, frameW, x, y) == Px.Red) { xs.Add(x); ys.Add(y); }
        if (xs.Count > 40)
        {
            xs.Sort(); ys.Sort();
            int lx = xs[xs.Count * 3 / 100], hx = xs[xs.Count * 97 / 100], ly = ys[ys.Count * 3 / 100], hy = ys[ys.Count * 97 / 100];
            double side = Math.Max(hx - lx, hy - ly);
            // a roughly round blob, a sensible size (2–14% of the game width)
            if (side > game.W * 0.02 && side < game.W * 0.14 && (hx - lx) > (hy - ly) * 0.45)
            {
                // the globe is round: full width tells its size even when the red has drained from the top
                double cx = (lx + hx) / 2.0, w = hx - lx, cyBottom = hy;
                var found = ((cx - w / 2 - game.X) / game.W, (cyBottom - w - game.Y) / game.H, w / game.W, w / game.H);
                if (_globe is null || w / game.W > _globe.Value.W * 0.9) _globe = found;
            }
        }
        if (_globe is not { } gl) return;

        // ---- read the globe ----
        int gx0 = (int)(game.X + gl.X * game.W), gy0 = (int)(game.Y + gl.Y * game.H);
        int gw = (int)(gl.W * game.W), gh = (int)(gl.H * game.H);
        int red = 0, gray = 0, yellow = 0, n = 0;
        for (int y = Math.Max(0, gy0); y < Math.Min(frameH, gy0 + gh); y += 2)
            for (int x = Math.Max(0, gx0); x < Math.Min(frameW, gx0 + gw); x += 2)
            {
                n++;
                switch (Class(bgra, frameW, x, y))
                {
                    case Px.Red: red++; break;
                    case Px.Gray: gray++; break;
                    case Px.Yellow: yellow++; break;
                }
            }
        if (n == 0) return;
        double fill = red / (double)n, grayR = gray / (double)n, yellowR = yellow / (double)n;
        LastFill = fill;

        if (fill > 0.08)
        {
            _lastAlive = now;
            _emptyStreak = 0;
            if (fill > 0.15) _armed = true;
            // calm or fighting: HP steady for a while = calm; the first change after that starts the fight
            if (_calmLevel < 0 || Math.Abs(fill - _calmLevel) > 0.02)
            {
                if (_calmLevel >= 0 && now - _calmSince >= CalmFor) _fightStart = now;
                _calmLevel = fill;
                _calmSince = now;
            }
            return;
        }

        bool empty = fill < 0.03 && grayR > 0.35 && yellowR > 0.004;
        if (!empty) { _emptyStreak = 0; return; }
        if (++_emptyStreak < 2 || !_armed || now - _lastAlive > TimeSpan.FromSeconds(20)) return;
        _armed = false;
        _emptyStreak = 0;
        var start = _fightStart == DateTime.MinValue || now - _fightStart > MaxFight ? now - TimeSpan.FromSeconds(90) : _fightStart;
        start -= BeforeFight;
        if (now - start > MaxFight) start = now - MaxFight;
        Debug?.Invoke($"death: HP globe empty (gray {grayR:0.00}, yellow {yellowR:0.000}); fight from {start:HH:mm:ss}");
        Died?.Invoke(start, now);
    }

    private enum Px { Other, Red, Gray, Yellow }

    private static Px Class(ReadOnlySpan<byte> f, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        int b = f[i], g = f[i + 1], r = f[i + 2];
        if (r > 140 && r > g * 1.8 && r > b * 1.8) return Px.Red;
        if (r > 180 && g > 150 && b < 110 && r - b > 90) return Px.Yellow;
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        if (mx - mn < 50 && (r + g + b) / 3 is > 100 and < 245) return Px.Gray;
        return Px.Other;
    }
}
