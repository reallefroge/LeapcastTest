using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LeapcastStudioAI;

/// <summary>What was picked for a YouTube output: a broadcast and its stream, the saved key, or nothing.</summary>
public sealed record YouTubePick(YouTubeBroadcast? Broadcast, YouTubeIngest? Ingest, bool UseSavedKey);

/// <summary>How the picker behaves (from Settings) and what it hands back.</summary>
public sealed class YouTubePickerOptions
{
    /// <summary>Show "go live on a new broadcast" (one click: made, then streamed to).</summary>
    public bool Quick { get; init; } = true;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Privacy { get; init; } = "public";
    /// <summary>Called with a new broadcast's title, description and privacy (to fill in next time).</summary>
    public Action<string, string, string>? RememberNew { get; init; }
    /// <summary>Saves the Google client (ID, secret) and returns the API for it: the one-time setup, right in the picker.</summary>
    public Func<string, string, YouTubeApi>? Setup { get; init; }
}

/// <summary>
/// "Which broadcast?" when going live on YouTube: the channel's live and scheduled broadcasts (soonest first), and
/// (when quick new broadcasts are on) a new one made and gone live on in one click. Picking a broadcast gets a
/// stream key that only that broadcast uses, so the video lands on it and not on another scheduled one.
/// </summary>
public static class YouTubePicker
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public const string CloudConsole = "https://console.cloud.google.com/apis/credentials";

    /// <returns>Null when cancelled.</returns>
    public static YouTubePick? Show(Window owner, YouTubeApi? api, string outputName, YouTubePickerOptions? options = null)
    {
        var opts = options ?? new YouTubePickerOptions();
        YouTubePick? result = null;
        List<YouTubeBroadcast> loaded = new();
        var cts = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(2, 0, 2, 8), FontSize = 12 };
        var list = new ListBox { Height = 220, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("TextBrush") };
        var signIn = new Button { Content = "Sign in with Google", Style = (Style)Application.Current.FindResource("PrimaryButton"), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };

        // ---- the one-time setup (no Google client yet)
        var setupId = new TextBox { Margin = new Thickness(0, 2, 0, 6) };
        var setupSecret = new PasswordBox { Margin = new Thickness(0, 2, 0, 6) };
        var setupSave = new Button { Content = "Save and sign in", Style = (Style)Application.Current.FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        var openConsole = new Button { Content = "Open Google Cloud console", Margin = new Thickness(8, 0, 0, 0) };
        var setupButtons = new StackPanel { Orientation = Orientation.Horizontal };
        setupButtons.Children.Add(setupSave); setupButtons.Children.Add(openConsole);
        var setup = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        setup.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("MutedBrush"), Margin = new Thickness(2, 0, 2, 8),
            Text = "To see your scheduled broadcasts, Leapcast reads them through YouTube's API with a free Google client of your own (one time, about 5 minutes):\n" +
                   "1) Open Google Cloud console → create a project.\n" +
                   "2) APIs & Services → Library → enable \"YouTube Data API v3\".\n" +
                   "3) OAuth consent screen → External → fill in the app name and your email → Publish app (\"In production\": a sign-in then lasts; in \"Testing\" it expires every 7 days).\n" +
                   "4) Credentials → Create credentials → OAuth client ID → Desktop app.\n" +
                   "5) Paste the client ID and secret below. Google then asks you to sign in once (it may say the app isn't verified: it's your own, so Advanced → continue).",
        });
        setup.Children.Add(new TextBlock { Text = "Client ID", FontSize = 12 });
        setup.Children.Add(setupId);
        setup.Children.Add(new TextBlock { Text = "Client secret", FontSize = 12 });
        setup.Children.Add(setupSecret);
        setup.Children.Add(setupButtons);

        // ---- a new broadcast, in one click
        string defaultTitle = opts.Title.Trim().Length > 0 ? opts.Title.Trim() : "Live now";
        var newTitle = new TextBox { Text = defaultTitle, MinWidth = 220 };
        var newDesc = new TextBox { Text = opts.Description, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 44, MaxHeight = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
        var privacy = new ComboBox { Width = 110, Margin = new Thickness(6, 0, 0, 0) };
        foreach (var p in new[] { "public", "unlisted", "private" }) privacy.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(p[0]) + p[1..], Tag = p });
        privacy.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "public", "unlisted", "private" }, opts.Privacy));
        var goNew = new Button { Content = "Go live on a new broadcast", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 190 };
        var newBox = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        newBox.Children.Add(new TextBlock { Text = "Or go live on a new broadcast", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 4) });
        var titleRow = new DockPanel();
        DockPanel.SetDock(privacy, Dock.Right);
        titleRow.Children.Add(privacy); titleRow.Children.Add(newTitle);
        newBox.Children.Add(titleRow);
        newBox.Children.Add(new TextBlock { Text = "Description (optional)", FontSize = 11, Foreground = Res("MutedBrush"), Margin = new Thickness(2, 6, 0, 0) });
        newBox.Children.Add(newDesc);
        newBox.Children.Add(goNew);
        newBox.Visibility = opts.Quick ? Visibility.Visible : Visibility.Collapsed;

        var go = new Button { Content = "Stream to this broadcast", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsEnabled = false, MinWidth = 170 };
        var useKey = new Button { Content = "Use my stream key instead" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(useKey, Dock.Left);
        DockPanel.SetDock(go, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(useKey);
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);

        var listBox = new Border { Child = list, Background = Res("FieldBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1) };
        var panel = new StackPanel { Margin = new Thickness(18), Width = 540 };
        panel.Children.Add(new TextBlock { Text = "Which YouTube broadcast?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 4) });
        panel.Children.Add(status);
        panel.Children.Add(setup);
        panel.Children.Add(signIn);
        panel.Children.Add(listBox);
        panel.Children.Add(newBox);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = outputName + ": choose the broadcast", Owner = owner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            SizeToContent = SizeToContent.WidthAndHeight, MaxHeight = SystemParameters.WorkArea.Height - 40,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("BgBrush"), Foreground = Res("TextBrush"),
        };

        bool ready = false;
        void Busy(bool on, string? text = null)
        {
            if (text is not null) status.Text = text;
            goNew.IsEnabled = !on && ready;
            go.IsEnabled = !on && ready && list.SelectedItem is ListBoxItem { Tag: YouTubeBroadcast };
        }

        void Mode(bool needsSetup)
        {
            setup.Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
            listBox.Visibility = needsSetup ? Visibility.Collapsed : Visibility.Visible;
            newBox.Visibility = !needsSetup && opts.Quick ? Visibility.Visible : Visibility.Collapsed;
        }

        ListBoxItem Row(YouTubeBroadcast b)
        {
            var sp = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            sp.Children.Add(new TextBlock { Text = b.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = $"{b.When} · {b.Privacy}", FontSize = 11, Foreground = b.LifeCycle == "live" ? Res("GoodBrush") : Res("MutedBrush") });
            return new ListBoxItem { Content = sp, Tag = b };
        }

        async Task LoadAsync()
        {
            ready = false;
            if (api is null)
            {
                Mode(true);
                Busy(false, "Pick the exact broadcast to stream to: set up YouTube's API once.");
                return;
            }
            Mode(false);
            if (!api.SignedIn)
            {
                status.Text = "Sign in with Google once so Leapcast can see your broadcasts (it opens in your browser).";
                signIn.Visibility = Visibility.Visible;
                list.Items.Clear();
                Busy(false);
                return;
            }
            signIn.Visibility = Visibility.Collapsed;
            Busy(true, "Loading your broadcasts…");
            try
            {
                loaded = await api.ListBroadcastsAsync(cts.Token);
                ready = true;
                list.Items.Clear();
                foreach (var b in loaded) list.Items.Add(Row(b));
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                // with nothing scheduled, Enter goes live on a new broadcast
                go.IsDefault = loaded.Count > 0 || !opts.Quick;
                goNew.IsDefault = loaded.Count == 0 && opts.Quick;
                if (goNew.IsDefault) goNew.Style = (Style)Application.Current.FindResource("PrimaryButton");
                Busy(false, loaded.Count == 0
                    ? opts.Quick ? "Nothing scheduled: give the new broadcast a title and go live." : "No scheduled or live broadcasts. Schedule one in YouTube Studio, or use your stream key (quick new broadcasts can be turned on in Settings → Connection)."
                    : "Pick the broadcast to stream to: it gets a stream key of its own, so the video goes to exactly that one.");
                if (goNew.IsDefault) { newTitle.Focus(); newTitle.SelectAll(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Busy(false, ex.Message);
                if (!api.SignedIn) await LoadAsync();
            }
        }

        async Task StreamToAsync(YouTubeBroadcast b)
        {
            Busy(true, "Getting the stream for \"" + b.Title + "\"…");
            var ingest = await api!.IngestForAsync(b, cts.Token, loaded);
            result = new YouTubePick(b, ingest, false);
            w.DialogResult = true;
        }

        list.SelectionChanged += (_, _) => go.IsEnabled = ready && list.SelectedItem is ListBoxItem { Tag: YouTubeBroadcast };
        async Task GoSelectedAsync()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: YouTubeBroadcast b }) return;
            try { await StreamToAsync(b); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Busy(false, ex.Message); }
        }

        async Task SignInAsync()
        {
            if (api is null) return;
            signIn.IsEnabled = false;
            status.Text = "Finish signing in in your browser…";
            try
            {
                await api.SignInAsync(url => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }, cts.Token);
                await LoadAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { signIn.IsEnabled = true; }
        }

        list.MouseDoubleClick += async (_, _) => { if (go.IsEnabled) await GoSelectedAsync(); };
        openConsole.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(CloudConsole) { UseShellExecute = true }); } catch { } };
        setupSave.Click += async (_, _) =>
        {
            if (setupId.Text.Trim().Length == 0) { status.Text = "Paste the client ID (it ends in .apps.googleusercontent.com)."; return; }
            if (opts.Setup is null) return;
            api = opts.Setup(setupId.Text.Trim(), setupSecret.Password.Trim());
            await LoadAsync();
            if (api is { SignedIn: false }) await SignInAsync();
        };
        signIn.Click += async (_, _) => await SignInAsync();
        goNew.Click += async (_, _) =>
        {
            if (api is null) return;
            Busy(true, "Making the broadcast…");
            try
            {
                string title = string.IsNullOrWhiteSpace(newTitle.Text) ? "Live now" : newTitle.Text.Trim();
                string priv = (privacy.SelectedItem as ComboBoxItem)?.Tag as string ?? "public";
                var b = await api.CreateBroadcastAsync(title, priv, cts.Token, newDesc.Text);
                opts.RememberNew?.Invoke(title, newDesc.Text.Trim(), priv);
                loaded.Insert(0, b);
                await StreamToAsync(b);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Busy(false, ex.Message); }
        };
        go.Click += async (_, _) => await GoSelectedAsync();
        useKey.Click += (_, _) => { result = new YouTubePick(null, null, true); w.DialogResult = true; };
        w.Loaded += async (_, _) => await LoadAsync();
        w.Closed += (_, _) => cts.Cancel();
        return w.ShowDialog() == true ? result : null;
    }
}
