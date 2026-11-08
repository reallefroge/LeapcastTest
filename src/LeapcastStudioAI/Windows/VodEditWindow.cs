using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LeapcastStudioAI.Vod;

namespace LeapcastStudioAI;

/// <summary>
/// Tools → "Edit a recording into a video…": pick a recording, a title and a length; the editor does the rest and
/// leaves the video, a thumbnail and a description (with chapters) in the VOD edits folder. Not modal: streaming
/// and everything else keep working while it runs (at low priority).
/// </summary>
public sealed class VodEditWindow : Window
{
    /// <summary>An edit is running (the stream health log notes it).</summary>
    public static volatile bool Busy;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private readonly StreamEngine _engine;
    private readonly StudioSettings _settings;
    private readonly Action _save;
    private readonly ListBox _list = new() { Height = 190 };
    private readonly TextBox _title = new(), _minutes = new() { Width = 60 };
    private readonly CheckBox _captions = new() { Content = "Captions", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _music = new() { Content = "Music (original, made for each video)", IsChecked = true };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 8, Maximum = 1, Margin = new Thickness(0, 12, 0, 6) };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Make the video", MinWidth = 150 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 80, IsEnabled = false };
    private readonly Button _open = new() { Content = "Open folder", Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cts;
    private string? _lastVideo;

    public VodEditWindow(StreamEngine engine, StudioSettings settings, Action save)
    {
        _engine = engine; _settings = settings; _save = save;
        Title = "Edit a recording into a video";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        Background = Res("BgBrush"); Foreground = Res("TextBrush");
        _start.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _title.Text = string.IsNullOrWhiteSpace(settings.RecapTitle) ? "Stream highlights" : settings.RecapTitle;
        _minutes.Text = settings.VodEditMinutes.ToString();
        _folder.Text = settings.VodEditFolder;
        _step.Foreground = Res("MutedBrush");

        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Turn a long stream into an edited video", FontSize = 16, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), FontSize = 12,
            Text = "Finds the best moments (your reactions, clips you saved, fights, chat) and edits them into a story: a cold open, chapters, captions, zooms, transitions, original music and sound effects. Recordings made in Leapcast edit best: what you said and did is logged while recording." });
        p.Children.Add(Label("Recording"));
        p.Children.Add(new Border { Child = _list, Background = Res("FieldBrush"), CornerRadius = new CornerRadius(8), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1) });
        var browse = new Button { Content = "Another file…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) => Browse();
        p.Children.Add(browse);
        p.Children.Add(Label("Video title"));
        p.Children.Add(_title);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Length (minutes)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_minutes);
        p.Children.Add(row);
        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        opts.Children.Add(_captions); opts.Children.Add(_music);
        p.Children.Add(opts);
        var folderRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var change = new Button { Content = "Change…", Margin = new Thickness(8, 0, 0, 0) };
        change.Click += (_, _) => ChooseFolder();
        DockPanel.SetDock(change, Dock.Right);
        folderRow.Children.Add(change);
        folderRow.Children.Add(new TextBlock { Text = "Save to: ", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush") });
        folderRow.Children.Add(_folder);
        p.Children.Add(folderRow);
        p.Children.Add(_bar);
        p.Children.Add(_step);
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_open, Dock.Left);
        DockPanel.SetDock(_start, Dock.Right);
        DockPanel.SetDock(_cancel, Dock.Right);
        var reviseOld = new Button { Content = "Revise an earlier edit…", Margin = new Thickness(8, 0, 0, 0) };
        reviseOld.Click += (_, _) => OpenEarlier();
        DockPanel.SetDock(reviseOld, Dock.Left);
        buttons.Children.Add(_open); buttons.Children.Add(reviseOld); buttons.Children.Add(_start); buttons.Children.Add(_cancel);
        p.Children.Add(buttons);
        p.Children.Add(BuildRevisePanel());
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _open.Click += (_, _) =>
        {
            try
            {
                if (_lastVideo is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastVideo}\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(_settings.VodEditFolder) { UseShellExecute = true });
            }
            catch { }
        };
        Closing += (_, e) =>
        {
            if (_cts is not null && MessageBox.Show(this, "Stop making the video?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
            else _cts?.Cancel();
        };
        LoadRecordings();
    }

    // ------------------------------------------------------------------ review & revise

    private VodProject? _project;
    private readonly Border _revise = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly TextBox _feedback = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _understood = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _rTitle = new(), _rMinutes = new() { Width = 60 };
    private readonly StackPanel _cards = new(), _moments = new();
    private readonly Slider _musicVol = new() { Minimum = 0, Maximum = 2, Width = 180, TickFrequency = 0.05, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _musicVolText = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _pace = new() { Width = 120, ItemsSource = new[] { "Tighter", "Normal", "Relaxed" } };
    private readonly ComboBox _zooms = new() { Width = 120, ItemsSource = new[] { "None", "Fewer", "Normal", "More" } };
    private readonly ComboBox _emphasis = new() { Width = 140, ItemsSource = new[] { "More action", "Balanced", "More funny" } };
    private readonly CheckBox _rSlow = new() { Content = "Slow motion", Margin = new Thickness(0, 0, 14, 0) }, _rFace = new() { Content = "Cuts to your face", Margin = new Thickness(0, 0, 14, 0) },
        _rCold = new() { Content = "Cold open", Margin = new Thickness(0, 0, 14, 0) }, _rCaptions = new() { Content = "Captions", Margin = new Thickness(0, 0, 14, 0) },
        _rMusic = new() { Content = "Music" };
    private readonly Button _rerender = new() { Content = "Re-render with changes", MinWidth = 190 };

    private FrameworkElement BuildRevisePanel()
    {
        _revise.Background = Res("FieldBrush"); _revise.BorderBrush = Res("LineBrush");
        _rerender.Style = (Style)Application.Current.FindResource("PrimaryButton");
        var r = new StackPanel();
        r.Children.Add(new TextBlock { Text = "Revise this video", FontSize = 15, FontWeight = FontWeights.SemiBold });
        r.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 8),
            Text = "Say what to change, or change it below. The recording isn't watched again and unchanged parts are reused, so a revision is quick." });
        r.Children.Add(Label("What should change?"));
        r.Children.Add(_feedback);
        r.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            Text = "e.g. “music quieter by 50%” · “rename part 2 to Khrysalis boss fight” · “remove the part at 3:20” · “add the moment at 1:12:30 of the stream” · “more funny moments” · “too many zooms” · “faster pace” · “make it 12 minutes”" });
        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), MinWidth = 90 };
        apply.Click += (_, _) => ApplyFeedback();
        r.Children.Add(apply);
        r.Children.Add(_understood);

        r.Children.Add(Label("Video title"));
        r.Children.Add(_rTitle);
        r.Children.Add(Label("Chapter cards (the big line, then the line under it)"));
        r.Children.Add(_cards);

        var sound = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        sound.Children.Add(new TextBlock { Text = "Music volume", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        sound.Children.Add(_musicVol); sound.Children.Add(_musicVolText);
        _musicVol.ValueChanged += (_, e) => _musicVolText.Text = $"{e.NewValue:P0}";
        r.Children.Add(sound);
        r.Children.Add(Row("Pace", _pace));
        r.Children.Add(Row("Zooms", _zooms));
        r.Children.Add(Row("Focus on", _emphasis));
        r.Children.Add(Row("Length (minutes)", _rMinutes));
        var toggles = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in new[] { _rCold, _rSlow, _rFace, _rCaptions, _rMusic }) toggles.Children.Add(c);
        r.Children.Add(toggles);
        r.Children.Add(Label("Moments in the video (untick to leave one out)"));
        r.Children.Add(new Border { Child = new ScrollViewer { Content = _moments, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6) });
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_rerender, Dock.Right);
        bar.Children.Add(_rerender);
        r.Children.Add(bar);
        _rerender.Click += async (_, _) => await ReviseAsync();
        _revise.Child = r;
        return _revise;
    }

    private FrameworkElement Row(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private void ShowRevise(VodProject project)
    {
        _project = project;
        _revise.Visibility = Visibility.Visible;
        _feedback.Text = "";
        _understood.Text = "";
        RefreshControls();
    }

    /// <summary>The controls show the project's current changes.</summary>
    private void RefreshControls()
    {
        if (_project is not { } pr) return;
        var t = pr.Tweaks;
        _rTitle.Text = t.Title ?? pr.Title;
        _cards.Children.Clear();
        foreach (var ch in pr.Chapters)
        {
            var card = t.Cards.FirstOrDefault(c => c.Chapter == ch.Index);
            var head = new TextBox { Width = 120, Text = card?.Heading ?? ch.Heading, Tag = ("h", ch.Index) };
            var sub = new TextBox { Text = card?.Sub ?? ch.Sub, Tag = ("s", ch.Index), Margin = new Thickness(8, 0, 0, 0) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = EditFeedback.Clock(ch.At), Width = 46, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush") });
            DockPanel.SetDock(row.Children[0], Dock.Left);
            row.Children.Add(head); DockPanel.SetDock(head, Dock.Left);
            row.Children.Add(sub);
            _cards.Children.Add(row);
        }
        if (pr.Chapters.Count == 0) _cards.Children.Add(new TextBlock { Text = "This video is short enough to have no chapter cards.", Foreground = Res("MutedBrush"), FontSize = 12 });
        _musicVol.Value = t.Music ? t.MusicVolume : 0;
        _musicVolText.Text = $"{_musicVol.Value:P0}";
        _pace.SelectedIndex = t.Pace + 1;
        _zooms.SelectedIndex = Math.Clamp(t.Zooms, 0, 3);
        _emphasis.SelectedIndex = t.Emphasis + 1;
        _rMinutes.Text = ((t.Minutes ?? pr.TargetSeconds / 60)).ToString("0.#");
        _rSlow.IsChecked = t.SlowMotion; _rFace.IsChecked = t.FaceCam; _rCold.IsChecked = t.ColdOpen;
        _rCaptions.IsChecked = t.Captions && pr.Captions; _rMusic.IsChecked = t.Music && pr.Music;
        _moments.Children.Clear();
        foreach (var m in pr.Moments)
        {
            bool removed = t.Remove.Any(r => r[0] <= m.From + 0.5 && r[1] >= m.To - 0.5);
            _moments.Children.Add(new CheckBox { IsChecked = !removed, Tag = m, Margin = new Thickness(0, 2, 0, 2),
                Content = $"{EditFeedback.Clock(m.At)}  ·  {(m.Quote.Length > 0 ? "“" + m.Quote + "”" : "(no words)")}  ·  from {EditFeedback.Clock(m.From)} of the recording" });
        }
    }

    /// <summary>The controls into the project's changes.</summary>
    private void ReadControls()
    {
        if (_project is not { } pr) return;
        var t = pr.Tweaks;
        string title = _rTitle.Text.Trim();
        t.Title = title.Length == 0 || title == pr.Title ? null : title;
        foreach (var row in _cards.Children.OfType<DockPanel>())
            foreach (var tb in row.Children.OfType<TextBox>())
                if (tb.Tag is ValueTuple<string, int> tag)
                {
                    var ch = pr.Chapters.First(c => c.Index == tag.Item2);
                    var card = t.Card(tag.Item2);
                    if (tag.Item1 == "h") card.Heading = tb.Text.Trim() == ch.Heading ? card.Heading : tb.Text.Trim();
                    else card.Sub = tb.Text.Trim() == ch.Sub ? card.Sub : tb.Text.Trim();
                }
        t.Music = _rMusic.IsChecked == true && _musicVol.Value > 0.001;
        if (t.Music) t.MusicVolume = _musicVol.Value;
        t.Pace = Math.Max(0, _pace.SelectedIndex) - 1;
        t.Zooms = Math.Max(0, _zooms.SelectedIndex);
        t.Emphasis = Math.Max(0, _emphasis.SelectedIndex) - 1;
        if (double.TryParse(_rMinutes.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mins) && mins is >= 1 and <= 120)
            t.Minutes = Math.Abs(mins - pr.TargetSeconds / 60) < 0.01 ? null : mins;
        t.SlowMotion = _rSlow.IsChecked == true; t.FaceCam = _rFace.IsChecked == true; t.ColdOpen = _rCold.IsChecked == true;
        t.Captions = _rCaptions.IsChecked == true;
        foreach (var cb in _moments.Children.OfType<CheckBox>())
            if (cb.Tag is EditMoment m)
            {
                t.Remove.RemoveAll(r => r[0] <= m.From + 0.5 && r[1] >= m.To - 0.5);
                if (cb.IsChecked != true) t.Remove.Add(new[] { m.From, m.To });
            }
    }

    private void ApplyFeedback()
    {
        if (_project is not { } pr || _feedback.Text.Trim().Length == 0) return;
        ReadControls(); // what's already been changed by hand stays
        var res = EditFeedback.Apply(_feedback.Text, pr);
        RefreshControls();
        var lines = res.Understood.Select(x => "✓ " + x).Concat(res.NotUnderstood.Select(x => "? Didn't catch: " + x)).ToList();
        _understood.Text = lines.Count == 0 ? "Nothing to change there." : string.Join(Environment.NewLine, lines)
            + (res.Understood.Count > 0 ? Environment.NewLine + "Press “Re-render with changes” when you're ready (you can adjust anything below first)." : "");
        _understood.Foreground = res.NotUnderstood.Count > 0 && res.Understood.Count == 0 ? Res("WarnBrush") : Res("TextBrush");
    }

    private async Task ReviseAsync()
    {
        if (_project is not { } pr) return;
        if (_engine.FfmpegPath is not { } ffmpeg) { _step.Text = "FFmpeg isn't available."; return; }
        if (_feedback.Text.Trim().Length > 0 && _understood.Text.Length == 0) ApplyFeedback(); // typed but not applied yet
        ReadControls();
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false; _rerender.IsEnabled = false; _cancel.IsEnabled = true; _open.Visibility = Visibility.Collapsed;
        _bar.Value = 0;
        var started = DateTime.Now;
        try
        {
            var progress = new Progress<VodEditor.Progress>(x => { _bar.Value = x.Fraction; _step.Text = x.Step; });
            VodRenderer.LogPath = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
            Busy = true;
            var result = await Task.Run(() => VodEditor.ReviseAsync(ffmpeg, pr, _engine.VodVideoArgs, progress, _cts.Token))
                .ContinueWith(t => { Busy = false; return t; }).Unwrap();
            _lastVideo = result.Video;
            _bar.Value = 1;
            _step.Text = $"Revised in {(DateTime.Now - started):h\\:mm\\:ss}: {Path.GetFileName(result.Video)} ({result.Length:m\\:ss}); {VodRenderer.Reused} of {pr.Pieces.Count} parts reused. The earlier version is still there.";
            _open.Visibility = Visibility.Visible;
            ShowRevise(pr);
        }
        catch (OperationCanceledException) { _step.Text = "Stopped."; _bar.Value = 0; }
        catch (Exception ex) { _step.Text = "Couldn't revise the video: " + ex.Message + " (details in logs\\vod-edit.log)"; VodRenderer.Note("Revise failed: " + ex); }
        finally { _cts = null; _start.IsEnabled = true; _rerender.IsEnabled = true; _cancel.IsEnabled = false; }
    }

    private void OpenEarlier()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Edited videos|*.mp4|All files|*.*", InitialDirectory = _settings.VodEditFolder, Title = "Pick an edited video to revise" };
        if (dlg.ShowDialog(this) != true) return;
        var pr = VodProject.Load(dlg.FileName);
        if (pr is null) { _step.Text = "That video wasn't made by this version of the editor (no revision info next to it). Make it again once and it can be revised from then on."; return; }
        if (!File.Exists(pr.Recording)) { _step.Text = "The recording that video came from isn't where it was: " + pr.Recording; return; }
        pr.Video = dlg.FileName;
        _lastVideo = dlg.FileName;
        _step.Text = "Revising " + Path.GetFileName(dlg.FileName);
        ShowRevise(pr);
    }

    private TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = 12 };

    private void LoadRecordings()
    {
        _list.Items.Clear();
        try
        {
            var files = new DirectoryInfo(_settings.RecordingFolder).Exists
                ? new DirectoryInfo(_settings.RecordingFolder).GetFiles("*.*").Where(f => f.Extension.ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".flv")
                    .Where(f => !f.Name.StartsWith("Replay ")).OrderByDescending(f => f.LastWriteTime).Take(40)
                : Enumerable.Empty<FileInfo>();
            foreach (var f in files) Add(f.FullName);
        }
        catch { }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        else _step.Text = "No recordings yet in " + _settings.RecordingFolder + ". Record a stream, or pick a file.";
    }

    private void Add(string path, bool select = false)
    {
        var f = new FileInfo(path);
        bool logged = File.Exists(VodSidecar.PathFor(path));
        var sp = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
        sp.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { FontSize = 11, Foreground = Res("MutedBrush"),
            Text = $"{f.LastWriteTime:ddd MMM d, h:mm tt} · {f.Length / 1048576.0 / 1024:0.0} GB{(logged ? " · session log ✓" : "")}" });
        var item = new ListBoxItem { Content = sp, Tag = path };
        if (select) { _list.Items.Insert(0, item); _list.SelectedItem = item; } else _list.Items.Add(item);
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Videos|*.mp4;*.mkv;*.mov;*.flv|All files|*.*", InitialDirectory = _settings.RecordingFolder };
        if (dlg.ShowDialog(this) == true) Add(dlg.FileName, true);
    }

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.VodEditFolder };
        if (dlg.ShowDialog(this) == true) { _settings.VodEditFolder = dlg.FolderName; _folder.Text = dlg.FolderName; _save(); }
    }

    private async Task RunAsync()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string recording }) { _step.Text = "Pick a recording first."; return; }
        if (_engine.FfmpegPath is not { } ffmpeg) { _step.Text = "FFmpeg isn't available."; return; }
        if (!int.TryParse(_minutes.Text.Trim(), out int minutes) || minutes < 1 || minutes > 120) { _step.Text = "Length: 1 to 120 minutes."; return; }
        _settings.VodEditMinutes = minutes;
        _save();
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false; _cancel.IsEnabled = true; _open.Visibility = Visibility.Collapsed;
        var started = DateTime.Now;
        try
        {
            var opts = new VodEditOptions(_title.Text.Trim().Length == 0 ? "Stream highlights" : _title.Text.Trim(), minutes * 60, _captions.IsChecked == true,
                _music.IsChecked == true, _settings.RecapSocials);
            var progress = new Progress<VodEditor.Progress>(x => { _bar.Value = x.Fraction; _step.Text = x.Step; });
            VodRenderer.LogPath = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
            if (BackgroundWork.Live) _step.Text = "You're live: editing slowly in the background so the stream stays smooth (full speed once you stop).";
            Busy = true;
            var result = await Task.Run(() => VodEditor.MakeAsync(ffmpeg, recording, _settings.VodEditFolder, opts, _engine.VodVideoArgs,
                new Progress<VodEditor.Progress>(x => { ((IProgress<VodEditor.Progress>)progress).Report(BackgroundWork.Live ? x with { Step = x.Step + " (slowed while you're live)" } : x); }), _cts.Token))
                .ContinueWith(t => { Busy = false; return t; }).Unwrap();
            _lastVideo = result.Video;
            if (VodEditor.LastProject is { } made) ShowRevise(made);
            _bar.Value = 1;
            _step.Text = $"Done in {(DateTime.Now - started):h\\:mm\\:ss}: {Path.GetFileName(result.Video)} ({result.Length:m\\:ss}), with a thumbnail and a description with chapters next to it."
                         + (VodRenderer.Fallbacks > 0 ? $" ({VodRenderer.Fallbacks} effect(s) were simplified; details in logs\\vod-edit.log)" : "");
            _open.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { _step.Text = "Stopped."; _bar.Value = 0; }
        catch (Exception ex)
        {
            _step.Text = "Couldn't make the video: " + ex.Message + " (details in logs\\vod-edit.log)";
            // the whole story goes to logs\vod-edit.log (the command, FFmpeg's full output, where it happened)
            try
            {
                string log = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {recording}{Environment.NewLine}{ex}{Environment.NewLine}{VodRenderer.LastFailure}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
        finally
        {
            _cts = null;
            _start.IsEnabled = true; _cancel.IsEnabled = false;
        }
    }
}
