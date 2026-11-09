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
        UploadBox.Text = state.Settings.UploadKbps > 0 ? state.Settings.UploadKbps.ToString(CultureInfo.InvariantCulture) : "";
        // scan by default unless it was measured recently
        ScanBox.IsChecked = state.Settings.UploadMeasuredUtc is not { } m || DateTime.UtcNow - m > TimeSpan.FromDays(7);
        if (state.Settings.UploadMeasuredUtc is { } when)
            UploadHint.Text = $"Measured {when.ToLocalTime():g} ({state.Settings.UploadKbps:N0} kbps). Scan again if your connection changed.";
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

    private DateTime? _scannedUtc;

    private int ParseUpload() =>
        int.TryParse(UploadBox.Text.Replace(",", "").Replace(" ", "").Replace("kbps", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? Math.Clamp(k, 0, 10_000_000) : 0;

    /// <summary>Measures the upload; returns kbps (also written into the box).</summary>
    private async Task<int> ScanAsync(Action<string> report, CancellationToken ct)
    {
        var r = await UploadScan.RunAsync(_state.Settings, _engine.FfmpegPath, new Progress<string>(report), ct);
        int kbps = (int)Math.Round(r.MeasuredKbps);
        UploadBox.Text = kbps.ToString(CultureInfo.InvariantCulture);
        _scannedUtc = DateTime.UtcNow;
        report(r.Detail);
        return kbps;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_engine.Live) { UploadHint.Text = "End your stream before scanning: the scan would compete with it for upload."; return; }
        _running = true;
        ScanButton.IsEnabled = PrimaryButton.IsEnabled = false;
        try { await ScanAsync(t => UploadHint.Text = t, _cts.Token); ScanBox.IsChecked = false; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UploadHint.Text = ex.Message; }
        finally { _running = false; ScanButton.IsEnabled = PrimaryButton.IsEnabled = true; }
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
        var scene = CurrentScene ?? throw new InvalidOperationException("Add a scene first.");
        // 0. upload speed (optional scan)
        if (ScanBox.IsChecked == true)
        {
            Step("… scanning your upload speed");
            try { int k = await ScanAsync(Replace, ct); Replace($"✓ Upload: {k:N0} kbps."); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Replace("• Upload scan: " + ex.Message); }
        }
        int upload = ParseUpload();
        if (upload == 0 && ScanBox.IsChecked != true) Step("• No upload speed given: the bitrate is set for a typical connection.");
        var sources = _state.Scenes.SelectMany(s => s.Sources).ToList();

        // 1. hardware
        var reasons = new List<string>();
        var (encoder, _) = SmartTuner.PickEncoder("Auto", _engine.Encoders, Environment.ProcessorCount, reasons);
        Step($"✓ {Environment.ProcessorCount} CPU threads; encoders: " +
             string.Join(", ", _engine.Encoders.Select(x => EncoderCatalog.DisplayName(x.Name))) + $". Using {EncoderCatalog.DisplayName(encoder)}.");

        // 2. the game picture (make it fill the stream if asked)
        var game = PipelineBuilder.VisibleLayers(scene.Sources)
            .FirstOrDefault(s => SourceTypes.KindOf(s.Type) is SourceKind.Window or SourceKind.Display)
            ?? scene.Sources.FirstOrDefault(s => s.Shown && SourceTypes.KindOf(s.Type) is SourceKind.Window or SourceKind.Display);
        if (game is not null && FillGameBox.IsChecked == true)
        {
            string key = PipelineBuilder.InputKey(game);
            foreach (var s in sources.Where(x => PipelineBuilder.InputKey(x) == key && x.Shown))
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
        int destinations = Math.Max(1, _state.Settings.Outputs.Count(o => o.Enabled && !o.IsVertical && o.OwnBitrateKbps <= 0));
        // the vertical canvas (if a vertical destination is on) and outputs with their own bitrate use upload too
        int vertical = _state.Settings.VerticalCanvas && _state.Settings.Outputs.Any(o => o.Enabled && o.IsVertical)
            ? (_state.Settings.VerticalBitrateKbps > 0 ? _state.Settings.VerticalBitrateKbps : 2500) : 0;
        vertical += _state.Settings.Outputs.Where(o => o.Enabled && o.OwnBitrateKbps > 0).Sum(o => o.OwnBitrateKbps + _state.Settings.AudioBitrateKbps);
        var ladder = AutoConfig.Ladder(new AutoConfig.Input(content, quality, upload, gameSize, _engine.Encoders,
            Environment.ProcessorCount, destinations, _state.Settings.AudioBitrateKbps, vertical));
        Step($"… stress test: your real scene, encoded like a stream, ~15 s per try (nothing goes live)");

        // 5. stress test, stepping down until it keeps up
        AutoConfig.Candidate? chosen = null;
        StreamEngine.BenchmarkResult? chosenResult = null;
        foreach (var c in ladder.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            Step($"… trying {c.Label}");
            var settings = c.Apply(_state.Settings, content);
            settings.UploadKbps = upload;
            settings.LowLatencyMode = false;
            await _engine.UpdateAsync(_state.Scenes.ToList(), scene, settings, false);
            var r = await _engine.BenchmarkAsync(settings, 10, ct);
            Replace($"{(r.Smooth ? "✓" : "✗")} {c.Label}: {r.Fps:0.0} of {r.TargetFps} FPS" +
                    (r.Speed > 0 ? $", {r.Speed:0.00}× real time" : "") + (r.Smooth ? " — smooth" : " — too heavy, trying lighter"));
            chosen = c; chosenResult = r;
            if (r.Smooth) break;
        }
        // 6. spare room on a graphics-card encoder: its slower, sharper settings give a better picture for the same
        // upload at no cost to the game; kept only if it still holds the full frame rate
        string preset = _state.Settings.EncoderPreset;
        string? presetNote = null;
        if (chosen is not null && chosenResult?.Smooth == true && chosen.Encoder != "libx264")
        {
            static int Rank(string p) => p switch { "Quality" => 2, "Balanced" => 1, _ => 0 };
            foreach (var p in new[] { "Quality", "Balanced" })
            {
                if (Rank(p) <= Rank(preset)) break;
                if (p == "Quality" && chosen.Encoder == "h264_amf") continue;   // AMD: "Quality" is the same as "Balanced"
                ct.ThrowIfCancellationRequested();
                Step($"… the encoder has room to spare: trying its sharper \"{p}\" settings");
                var settings = chosen.Apply(_state.Settings, content);
                settings.UploadKbps = upload;
                settings.LowLatencyMode = false;
                settings.EncoderPreset = p;
                await _engine.UpdateAsync(_state.Scenes.ToList(), scene, settings, false);
                var r = await _engine.BenchmarkAsync(settings, 10, ct);
                bool holds = r.Smooth && r.Fps >= r.TargetFps * 0.97;
                Replace($"{(holds ? "✓" : "✗")} \"{p}\" encoder settings: {r.Fps:0.0} of {r.TargetFps} FPS" +
                        (r.Speed > 0 ? $", {r.Speed:0.00}× real time" : "") + (holds ? " — sharper picture, same upload" : " — too heavy, keeping the faster settings"));
                if (holds) { preset = p; presetNote = $"Encoder settings: \"{p}\" (your graphics card had room for a sharper picture at the same bitrate)."; break; }
            }
        }
        await _engine.UpdateAsync(_state.Scenes.ToList(), scene, _state.Settings, false);
        if (chosen is null) throw new InvalidOperationException("Nothing could be tested.");

        var result = chosen.Apply(_state.Settings, content);
        result.EncoderPreset = preset;
        result.UploadKbps = upload;
        if (_scannedUtc is { } scanned && ParseUpload() == upload) result.UploadMeasuredUtc = scanned;
        else if (upload != _state.Settings.UploadKbps) result.UploadMeasuredUtc = null;
        result.LowLatencyMode = false;
        Result = result;

        SummaryCard.Visibility = Visibility.Visible;
        SummaryTitle.Text = chosen.Label + (chosenResult?.Smooth == true ? "" : " (lightest option: close other heavy programs while streaming)");
        var list = chosen.Reasons.ToList();
        list.Add("Smart VBR: static screens use far less upload; low-latency mode off (it costs quality per kbps).");
        if (presetNote is not null) list.Add(presetNote);
        if (game is not null && FillGameBox.IsChecked == true) list.Add($"\"{game.Name}\" fills the whole stream: no bars, no stretching.");
        if (_state.Settings.Outputs.Count(o => o.Enabled) > 1) list.Add("All your platforms share this one encode.");
        SummaryList.ItemsSource = list;
        _done = true;
        PrimaryButton.Content = "Apply";
        CancelButton.Content = "Discard";
        if (_camera is not null && scene.Sources.Any(s => s.Shown && SourceTypes.KindOf(s.Type) == SourceKind.Microphone))
            ClapButton.Visibility = Visibility.Visible;
    }

    /// <summary>Measures the camera delay with a few claps and fixes it (sync offset on that camera).</summary>
    private async void Clap_Click(object sender, RoutedEventArgs e)
    {
        if (_camera is null || _running) return;
        var scene = CurrentScene;
        if (scene is null) return;
        if (MessageBox.Show(this, "Click OK, then clap 4–5 times, once a second, in view of the camera.",
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
