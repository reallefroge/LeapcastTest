using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LeapcastStudioAI;

/// <summary>
/// Auto-config: measures the game window, picks the encoder and a camera mode, then stress-tests the real scene
/// (capture + every scene + encode) and steps down until it keeps up, so the stream is smooth while you play.
/// </summary>
public partial class AutoConfigWindow : Window
{
    private readonly StreamEngine _engine;
    private readonly StudioState _state;
    private readonly ObservableCollection<string> _steps = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Action> _undo = new();
    private bool _running, _done, _applied;
    private StudioSource? _camera;

    /// <summary>The settings to save when the user clicks Apply (sources are already updated in place).</summary>
    public StudioSettings? Result { get; private set; }

    public AutoConfigWindow(StreamEngine engine, StudioState state)
    {
        InitializeComponent();
        _engine = engine;
        _state = state;
        StepsList.ItemsSource = _steps;
        SelectTag(ContentBox, state.Settings.ContentType);
        SelectTag(PriorityBox, "Performance");
        UploadBox.Text = state.Settings.UploadMbps > 0 ? state.Settings.UploadMbps.ToString("0.#", CultureInfo.CurrentCulture) : "";
        if (state.Settings.UploadMeasuredUtc is { } when)
            UploadHint.Text = $"Measured {when.ToLocalTime():g}. Change it if your connection changed.";
        Closed += (_, _) =>
        {
            _cts.Cancel();
            // closed without applying: put back anything the test changed on the sources
            if (!_applied) foreach (var u in _undo.AsEnumerable().Reverse()) u();
        };
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void Step(string text) => _steps.Add(text);

    private void Replace(string text)
    {
        if (_steps.Count > 0) _steps[^1] = text; else _steps.Add(text);
    }

    private StudioScene? CurrentScene => _state.Scenes.FirstOrDefault(s => s.Id == _state.SelectedSceneId) ?? _state.Scenes.FirstOrDefault();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            _applied = Result is not null;
            DialogResult = _applied;
            return;
        }
        if (_running) return;
        if (_engine.Live)
        {
            MessageBox.Show(this, "End the stream first: the test needs your PC to itself for a minute.", "Auto-config", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _running = true;
        PrimaryButton.IsEnabled = false;
        QuestionsPage.Visibility = Visibility.Collapsed;
        ResultsPage.Visibility = Visibility.Visible;
        try { await RunAsync(_cts.Token); }
        catch (OperationCanceledException) { Step("Stopped."); }
        catch (Exception ex) { Step("✗ " + ex.Message); }
        finally
        {
            _running = false;
            PrimaryButton.IsEnabled = true;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string content = TagOf(ContentBox, "LowMotion");
        bool quality = TagOf(PriorityBox, "Performance") == "Quality";
        double upload = double.TryParse(UploadBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ? Math.Max(0, up) : 0;
        var scene = CurrentScene ?? throw new InvalidOperationException("Add a scene first.");
        var sources = _state.Scenes.SelectMany(s => s.Sources).ToList();

        // 1. hardware
        var reasons = new List<string>();
        var (encoder, _) = SmartTuner.PickEncoder("Auto", _engine.Encoders, Environment.ProcessorCount, reasons);
        Step($"✓ {Environment.ProcessorCount} CPU threads; encoders: " +
             string.Join(", ", _engine.Encoders.Select(x => EncoderCatalog.DisplayName(x.Name))) + $". Using {EncoderCatalog.DisplayName(encoder)}.");

        // 2. the game picture (make it fill the stream if asked)
        var game = PipelineBuilder.VisibleLayers(scene.Sources)
            .FirstOrDefault(s => SourceTypes.KindOf(s.Type) is SourceKind.Window or SourceKind.Display)
            ?? scene.Sources.FirstOrDefault(s => s.Visible && SourceTypes.KindOf(s.Type) is SourceKind.Window or SourceKind.Display);
        if (game is not null && FillGameBox.IsChecked == true)
        {
            string key = PipelineBuilder.InputKey(game);
            foreach (var s in sources.Where(x => PipelineBuilder.InputKey(x) == key && x.Visible))
            {
                var (anchor, fill) = (s.Anchor, s.FillMode);
                _undo.Add(() => { s.Anchor = anchor; s.FillMode = fill; });
                s.Anchor = Anchors.Fill;
                s.FillMode = Anchors.FillCrop;
            }
        }
        (int W, int H)? gameSize = null;
        if (game is not null)
        {
            Step($"… measuring \"{game.Name}\"");
            gameSize = _engine.CaptureSize(game);
            if (gameSize is null)
            {
                // run the preview briefly so the capture reports the real size
                bool hadPreview = _engine.PreviewOn;
                await _engine.UpdateAsync(_state.Scenes.ToList(), scene, _state.Settings, false);
                if (!hadPreview) await _engine.SetPreviewAsync(true);
                for (int i = 0; i < 20 && (gameSize = _engine.CaptureSize(game)) is null; i++) await Task.Delay(250, ct);
                if (!hadPreview) await _engine.SetPreviewAsync(false);
            }
            Replace(gameSize is { } g
                ? $"✓ \"{game.Name}\" draws {g.W}×{g.H}" + (FillGameBox.IsChecked == true ? ": it will fill the whole 16:9 stream (edges trimmed, never stretched)." : ".")
                : $"✓ \"{game.Name}\": size not available yet (is the game open?). Using standard sizes.");
        }
        else Step("• No game capture in this scene: using standard sizes.");

        // 3. webcam mode: the stream's frame rate, no bigger than it's drawn
        _camera = PipelineBuilder.VisibleLayers(scene.Sources).FirstOrDefault(s => SourceTypes.KindOf(s.Type) == SourceKind.Camera && s.DeviceName.Length > 0);
        int streamFps = content == "HighMotion" ? 60 : 30;
        if (_camera is not null && _engine.FfmpegPath is { } ff)
        {
            Step($"… checking \"{_camera.Name}\" modes");
            var modes = await CameraModes.ListAsync(ff, _camera.DeviceName);
            int cw = (int)LayoutMath.Horizontal(_camera, 1920, 1080, LayoutMath.EstimateAspect(_camera)).W;
            if (_state.Settings.VerticalCanvas) cw = Math.Max(cw, (int)(720 * 0.92));
            var best = CameraModes.Best(modes, cw, Math.Min(streamFps, 30));
            if (best is null) Replace($"• \"{_camera.Name}\": couldn't read its modes, leaving it as it is.");
            else
            {
                string dev = _camera.DeviceName;
                foreach (var s in sources.Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.DeviceName == dev))
                {
                    var (res, fps, fmt) = (s.CameraResolution, s.CameraFps, s.CameraFormat);
                    _undo.Add(() => { s.CameraResolution = res; s.CameraFps = fps; s.CameraFormat = fmt; });
                    s.CameraResolution = $"{best.Width}x{best.Height}";
                    s.CameraFps = (int)Math.Round(Math.Min(best.MaxFps, 30));
                    s.CameraFormat = best.Tag;
                }
                Replace($"✓ \"{_camera.Name}\": {best} (matches the stream's frame rate: no repeated frames).");
            }
        }

        // 4. candidates, best first
        int destinations = Math.Max(1, _state.Settings.Outputs.Count(o => o.Enabled && !o.IsVertical));
        var ladder = AutoConfig.Ladder(new AutoConfig.Input(content, quality, upload, gameSize, _engine.Encoders,
            Environment.ProcessorCount, destinations, _state.Settings.AudioBitrateKbps));
        Step($"… stress test: your real scene, encoded like a stream, ~15 s per try (nothing goes live)");

        // 5. stress test, stepping down until it keeps up
        AutoConfig.Candidate? chosen = null;
        StreamEngine.BenchmarkResult? chosenResult = null;
        foreach (var c in ladder.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            Step($"… trying {c.Label}");
            var settings = c.Apply(_state.Settings, content);
            settings.UploadMbps = upload;
            settings.LowLatencyMode = false;
            await _engine.UpdateAsync(_state.Scenes.ToList(), scene, settings, false);
            var r = await _engine.BenchmarkAsync(settings, 10, ct);
            Replace($"{(r.Smooth ? "✓" : "✗")} {c.Label}: {r.Fps:0.0} of {r.TargetFps} FPS" +
                    (r.Speed > 0 ? $", {r.Speed:0.00}× real time" : "") + (r.Smooth ? " — smooth" : " — too heavy, trying lighter"));
            chosen = c; chosenResult = r;
            if (r.Smooth) break;
        }
        await _engine.UpdateAsync(_state.Scenes.ToList(), scene, _state.Settings, false);
        if (chosen is null) throw new InvalidOperationException("Nothing could be tested.");

        var result = chosen.Apply(_state.Settings, content);
        result.UploadMbps = upload;
        if (upload > 0 && Math.Abs(upload - _state.Settings.UploadMbps) > 0.05) result.UploadMeasuredUtc = null;
        result.LowLatencyMode = false;
        Result = result;

        SummaryCard.Visibility = Visibility.Visible;
        SummaryTitle.Text = chosen.Label + (chosenResult?.Smooth == true ? "" : " (lightest option: close other heavy programs while streaming)");
        var list = chosen.Reasons.ToList();
        list.Add("Smart VBR: static screens use far less upload; low-latency mode off (it costs quality per kbps).");
        if (game is not null && FillGameBox.IsChecked == true) list.Add($"\"{game.Name}\" fills the whole stream: no bars, no stretching.");
        if (_state.Settings.Outputs.Count(o => o.Enabled) > 1) list.Add("All your platforms share this one encode.");
        SummaryList.ItemsSource = list;
        _done = true;
        PrimaryButton.Content = "Apply";
        CancelButton.Content = "Discard";
        if (_camera is not null && scene.Sources.Any(s => s.Visible && SourceTypes.KindOf(s.Type) == SourceKind.Microphone))
            ClapButton.Visibility = Visibility.Visible;
    }

    /// <summary>Measures the camera delay with a few claps and fixes it (sync offset on that camera).</summary>
    private async void Clap_Click(object sender, RoutedEventArgs e)
    {
        if (_camera is null || _running) return;
        var scene = CurrentScene;
        if (scene is null) return;
        if (MessageBox.Show(this, "When you click OK, clap 4–5 times, about once a second, where the camera can see your hands.",
                "Camera sync", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _running = true;
        ClapButton.IsEnabled = PrimaryButton.IsEnabled = false;
        try
        {
            Step("… listening and watching: clap now");
            await _engine.UpdateAsync(_state.Scenes.ToList(), scene, Result ?? _state.Settings, false);
            bool hadPreview = _engine.PreviewOn;
            if (!hadPreview) await _engine.SetPreviewAsync(true);
            var m = await _engine.MeasureCameraSyncAsync(_camera, 8, _cts.Token);
            if (!hadPreview) await _engine.SetPreviewAsync(false);
            if (m is not { Reliable: true })
            {
                Replace("✗ Couldn't see and hear the claps clearly. Try again a bit louder, with your hands in view.");
                return;
            }
            int add = (int)Math.Round(m.OffsetMs);
            string dev = _camera.DeviceName;
            foreach (var s in _state.Scenes.SelectMany(x => x.Sources).Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.DeviceName == dev))
                s.SyncOffsetMs = Math.Clamp(s.SyncOffsetMs + add, -500, 1500);
            Replace(Math.Abs(add) < 40
                ? $"✓ Camera and microphone are in sync ({add:+0;-0} ms)."
                : $"✓ The camera was {Math.Abs(add)} ms {(add > 0 ? "behind" : "ahead of")} your voice: corrected (sync offset {_camera.SyncOffsetMs} ms).");
        }
        catch (Exception ex) { Replace("✗ " + ex.Message); }
        finally
        {
            _running = false;
            ClapButton.IsEnabled = PrimaryButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false; // Closed puts the sources back
    }
}
