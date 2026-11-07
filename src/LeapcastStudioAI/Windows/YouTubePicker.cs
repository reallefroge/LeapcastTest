using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LeapcastStudioAI;

/// <summary>What was picked for a YouTube output: a broadcast and its stream, the saved key, or nothing.</summary>
public sealed record YouTubePick(YouTubeBroadcast? Broadcast, YouTubeIngest? Ingest, bool UseSavedKey);

/// <summary>
/// "Which broadcast?" when going live on YouTube: the channel's live and scheduled broadcasts (soonest first),
/// or a new one. Picking one gets the exact stream YouTube expects for it, so the video lands on that broadcast.
/// </summary>
public static class YouTubePicker
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <returns>Null when cancelled.</returns>
    public static YouTubePick? Show(Window owner, YouTubeApi api, string outputName)
    {
        YouTubePick? result = null;
        var cts = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(2, 0, 2, 8), FontSize = 12 };
        var list = new ListBox { Height = 260, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("TextBrush") };
        var signIn = new Button { Content = "Sign in with Google", Style = (Style)Application.Current.FindResource("PrimaryButton"), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        var newTitle = new TextBox { Text = "Live now", MinWidth = 220 };
        var privacy = new ComboBox { Width = 110, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var p in new[] { "public", "unlisted", "private" }) privacy.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(p[0]) + p[1..], Tag = p });
        privacy.SelectedIndex = 0;
        var create = new Button { Content = "New broadcast" };
        var go = new Button { Content = "Stream to this broadcast", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsEnabled = false, MinWidth = 170 };
        var useKey = new Button { Content = "Use my stream key instead" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        var newRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(create, Dock.Right);
        DockPanel.SetDock(privacy, Dock.Right);
        newRow.Children.Add(create);
        newRow.Children.Add(privacy);
        newRow.Children.Add(newTitle);
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(useKey, Dock.Left);
        DockPanel.SetDock(go, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(useKey);
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18), Width = 520 };
        panel.Children.Add(new TextBlock { Text = "Which YouTube broadcast?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 4) });
        panel.Children.Add(status);
        panel.Children.Add(signIn);
        panel.Children.Add(new Border { Child = list, Background = Res("FieldBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1) });
        panel.Children.Add(newRow);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = outputName + ": choose the broadcast", Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("BgBrush"), Foreground = Res("TextBrush"),
        };

        void Busy(bool on, string? text = null)
        {
            if (text is not null) status.Text = text;
            create.IsEnabled = !on;
            go.IsEnabled = !on && list.SelectedItem is ListBoxItem { Tag: YouTubeBroadcast };
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
            if (!api.SignedIn)
            {
                status.Text = "Sign in with Google once so Leapcast can see your broadcasts (it opens in your browser).";
                signIn.Visibility = Visibility.Visible;
                list.Items.Clear();
                Busy(false);
                create.IsEnabled = false;
                return;
            }
            signIn.Visibility = Visibility.Collapsed;
            Busy(true, "Loading your broadcasts…");
            try
            {
                var all = await api.ListBroadcastsAsync(cts.Token);
                list.Items.Clear();
                foreach (var b in all) list.Items.Add(Row(b));
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                Busy(false, all.Count == 0
                    ? "No scheduled or live broadcasts. Make a new one below, or use your stream key."
                    : "Pick the broadcast to stream to. Scheduled ones go live when the video arrives.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Busy(false, ex.Message);
                if (!api.SignedIn) await LoadAsync();
            }
        }

        list.SelectionChanged += (_, _) => go.IsEnabled = list.SelectedItem is ListBoxItem { Tag: YouTubeBroadcast };
        signIn.Click += async (_, _) =>
        {
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
        };
        create.Click += async (_, _) =>
        {
            Busy(true, "Creating the broadcast…");
            try
            {
                string title = string.IsNullOrWhiteSpace(newTitle.Text) ? "Live now" : newTitle.Text.Trim();
                var b = await api.CreateBroadcastAsync(title, (privacy.SelectedItem as ComboBoxItem)?.Tag as string ?? "public", cts.Token);
                var item = Row(b);
                list.Items.Insert(0, item);
                list.SelectedItem = item;
                Busy(false, $"Made \"{b.Title}\". It goes live when the video arrives.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Busy(false, ex.Message); }
        };
        go.Click += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: YouTubeBroadcast b }) return;
            Busy(true, "Getting the stream for \"" + b.Title + "\"…");
            go.IsEnabled = false;
            try
            {
                var ingest = await api.IngestForAsync(b, cts.Token);
                result = new YouTubePick(b, ingest, false);
                w.DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Busy(false, ex.Message); }
        };
        useKey.Click += (_, _) => { result = new YouTubePick(null, null, true); w.DialogResult = true; };
        w.Loaded += async (_, _) => await LoadAsync();
        w.Closed += (_, _) => cts.Cancel();
        return w.ShowDialog() == true ? result : null;
    }
}
