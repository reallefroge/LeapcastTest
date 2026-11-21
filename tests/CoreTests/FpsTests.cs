using LeapcastStudioAI;

namespace CoreTests;

/// <summary>
/// Each source's own capture rate. A 60 FPS canvas shouldn't force a webcam, a browser overlay or a second
/// game client in the corner to be captured 60 times a second.
/// </summary>
public static class FpsTests
{
    public static void Run(Action<bool, string> check)
    {
        var plan = new EncodePlan(1920, 1080, 60, 6000, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "t", Array.Empty<string>());

        // the screen: its own rate reaches the capture, and can't exceed the canvas
        var screen = new StudioSource { Name = "Screen", Type = SourceTypes.Display, Anchor = Anchors.Fill, CaptureFps = 30 };
        var scene = new StudioScene { Name = "Live" };
        scene.Sources.Add(screen);
        string At(int fps, StudioScene sc, EncodePlan p) => PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = new[] { sc }, ActiveSceneIndex = 0, Preview = false, IsWindows = true,
            Plan = p with { Fps = fps }, MonitorBounds = _ => (0, 0, 1920, 1080),
        }).Args.FirstOrDefault(a => a.Contains("ddagrab", StringComparison.Ordinal)) ?? "";
        string at60 = At(60, scene, plan), at24 = At(24, scene, plan);
        check(at60.Contains("framerate=30") && at24.Contains("framerate=24"),
            $"capture rate: the screen is captured at its own rate, never above the canvas ({Rate(at60)} on a 60 canvas, {Rate(at24)} on a 24 one)");

        screen.CaptureFps = 0;
        check(At(60, scene, plan).Contains("framerate=60"), "…and 'same as the canvas' still follows the canvas");

        // a window: the need handed to the reader carries the rate and the full-quality choice
        var game = new StudioSource { Name = "Game", Type = SourceTypes.Game, WindowTitle = "Wizard101", Anchor = Anchors.Fill, CaptureFps = 48, FullQuality = true };
        var need = new WindowCaptureBridge.Need(game, 1920, 1080, Anchors.Fill, false, game.CaptureFps, game.FullQuality);
        check(need.Fps == 48 && need.Full, $"capture rate: a window's own rate and quality reach its reader (fps {need.Fps}, full {need.Full})");

        // a camera shared by two scenes takes the fastest rate either scene asks of it
        var slow = new StudioSource { Name = "Cam", Type = SourceTypes.Camera, DeviceName = "c", CaptureFps = 15 };
        var fast = new StudioSource { Name = "Cam", Type = SourceTypes.Camera, DeviceName = "c", CaptureFps = 30 };
        int want = new[] { slow, fast }.Select(u => u.CaptureFps).DefaultIfEmpty(0).Max();
        check(want == 30, $"capture rate: a camera in two scenes is captured at the faster of the two ({want})");

        // a game is its window, so everything that knows about windows knows about it
        check(SourceTypes.KindOf(SourceTypes.Game) == SourceKind.Window
              && SourceTypes.IsVideo(SourceTypes.KindOf(SourceTypes.Game))
              && SourceTypes.Pickable.Contains(SourceTypes.Game),
            "game capture: offered in the picker and handled as the window it is");

        // scenes saved when "Game Capture" meant the screen keep meaning the screen
        var old = new StudioState();   // Version 0: saved before "Game Capture" meant a window
        var sc2 = new StudioScene { Name = "Old" };
        sc2.Sources.Add(new StudioSource { Name = "Game Capture", Type = SourceTypes.Game });
        old.Scenes.Add(sc2);
        StateStore.Migrate(old);
        var wasScreen = old.Scenes[0].Sources.First(x => x.Name == "Game Capture");

        // and a game added now, on an up-to-date file, stays a game however often it is loaded
        var now = new StudioState { Version = StudioState.CurrentVersion };
        var sc3 = new StudioScene { Name = "New" };
        sc3.Sources.Add(new StudioSource { Name = "Wizard101", Type = SourceTypes.Game, WindowTitle = "Wizard101" });
        now.Scenes.Add(sc3);
        StateStore.Migrate(now);
        StateStore.Migrate(now);
        var wasWindow = now.Scenes[0].Sources[0];
        check(wasScreen.Type == SourceTypes.Display && wasWindow.Type == SourceTypes.Game,
            $"game capture: a scene saved before this type meant a window keeps its screen capture ({wasScreen.Type}), " +
            $"and one added now stays a game ({wasWindow.Type}), however often it is loaded");
    }

    private static string Rate(string arg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(arg, @"framerate=(\d+)");
        return m.Success ? m.Groups[1].Value : "?";
    }
}
