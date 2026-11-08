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
        buttons.Children.Add(_open); buttons.Children.Add(_start); buttons.Children.Add(_cancel);
        p.Children.Add(buttons);
        Content = p;

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
            var result = await Task.Run(() => VodEditor.MakeAsync(ffmpeg, recording, _settings.VodEditFolder, opts, _engine.VodVideoArgs, progress, _cts.Token));
            _lastVideo = result.Video;
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
