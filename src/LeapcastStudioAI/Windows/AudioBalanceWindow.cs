using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LeapcastStudioAI;

/// <summary>
/// "Balance my audio": talk for a few seconds, let the game play for a few, and the microphone and game faders
/// (and the stream output) are set so your voice is at a good streaming level with the game under it.
/// </summary>
public static class AudioBalanceWindow
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private const int Seconds = 8;

    /// <param name="level">A source's meter right now (dBFS before its fader).</param>
    /// <returns>The levels to apply, or null when cancelled.</returns>
    public static AudioBalance.Result? Show(Window owner, string micName, string gameName, Func<double> micLevel, Func<double> gameLevel, double micNow, double gameNow)
    {
        AudioBalance.Result? result = null;
        var title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), Text = "Balance my audio" };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = $"Two short tests ({Seconds} seconds each) and Leapcast sets the faders for you:\n1) You talk the way you do on stream (\"{micName}\").\n2) The game plays with its normal sound (\"{gameName}\").\nYour voice ends up at a healthy streaming level with the game clearly under it." };
        var bar = new ProgressBar { Height = 10, Minimum = -60, Maximum = 0, Value = -60, Margin = new Thickness(0, 0, 0, 4) };
        var count = new TextBlock { Foreground = Res("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        var go = new Button { Content = "Start: I'll talk now", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 170 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(go);
        var panel = new StackPanel { Margin = new Thickness(18), Width = 460 };
        foreach (var c in new UIElement[] { title, text, bar, count, buttons }) panel.Children.Add(c);
        var w = new Window { Title = "Balance my audio", Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Res("BgBrush"), Foreground = Res("TextBrush") };

        var mic = new List<double>(); var game = new List<double>();
        int step = 0;                       // 0 intro, 1 talking, 2 between, 3 game, 4 result
        DateTime until = DateTime.MinValue;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (step is not (1 or 3)) return;
            double lv = step == 1 ? micLevel() : gameLevel();
            (step == 1 ? mic : game).Add(lv);
            bar.Value = Math.Clamp(lv, -60, 0);
            double left = (until - DateTime.Now).TotalSeconds;
            count.Text = step == 1 ? $"Listening to you… {Math.Max(0, left):0} s (keep talking, as loud as you get on stream)" : $"Listening to the game… {Math.Max(0, left):0} s";
            if (left > 0) return;
            bar.Value = -60;
            if (step == 1)
            {
                step = 2; go.IsEnabled = true; go.Content = "Next: the game is playing";
                text.Text = "Got your voice. Now stop talking and let the game make its normal sound (music, a fight), then press the button.";
                count.Text = "";
            }
            else
            {
                step = 4;
                result = AudioBalance.Suggest(mic, game, micNow, gameNow);
                string Db(double d) => (d > 0 ? "+" : "") + d.ToString("0.#") + " dB";
                text.Text = $"Suggested levels:\n• {micName}: {Db(result.MicDb)} (was {Db(micNow)})\n• {gameName}: {Db(result.GameDb)} (was {Db(gameNow)})\n• Stream output: 0 dB\n\n" +
                            "Your voice's loud moments will peak around -6 dB with the game about 12 dB under it."
                            + (result.MicNote is { } mn ? "\n\n" + mn : "") + (result.GameNote is { } gn ? "\n\n" + gn : "");
                count.Text = "";
                go.IsEnabled = true; go.Content = "Apply these levels";
            }
        };
        go.Click += (_, _) =>
        {
            if (step == 4) { w.DialogResult = true; return; }
            step = step == 0 ? 1 : 3;
            until = DateTime.Now.AddSeconds(Seconds);
            go.IsEnabled = false;
            text.Text = step == 1 ? "Talk now, the way you do on stream: normal voice, and a loud moment or two." : "Let the game play. Don't talk.";
        };
        w.Loaded += (_, _) => timer.Start();
        w.Closed += (_, _) => timer.Stop();
        return w.ShowDialog() == true ? result : null;
    }
}
