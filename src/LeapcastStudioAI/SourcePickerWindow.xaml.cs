using System.Windows;
using System.Windows.Input;

namespace LeapcastStudioAI;

public partial class SourcePickerWindow : Window
{
    public sealed record Choice(string Title, string Description);

    private static readonly Choice[] Choices =
    {
        new(SourceTypes.Window, "One game window (e.g. Wizard101), captured by the GPU. Fills the stream at any window size and survives the game recreating its window."),
        new(SourceTypes.Display, "Your whole screen via GPU capture (fullscreen games, several windows at once)."),
        new(SourceTypes.Camera, "Webcam overlay, denoised before encoding to save bandwidth."),
        new(SourceTypes.Browser, "A web page: your chat, alerts or any widget URL, with a transparent background."),
        new(SourceTypes.Image, "A PNG/JPG overlay: frame, logo, \"starting soon\" screen."),
        new(SourceTypes.Text, "A text label rendered in Segoe UI with a drop shadow."),
        new(SourceTypes.Microphone, "Your mic, with adaptive background-noise removal."),
        new(SourceTypes.DesktopAudio, "Everything you hear: game sound, music, alerts."),
    };

    public string? SelectedType { get; private set; }

    public SourcePickerWindow()
    {
        InitializeComponent();
        // every pickable type must be listed here (keeps the picker in step with SourceTypes.Pickable)
        SourceType.ItemsSource = Choices.OrderBy(c => Array.IndexOf(SourceTypes.Pickable, c.Title)).ToArray();
        SourceType.SelectedIndex = 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedType = (SourceType.SelectedItem as Choice)?.Title;
        DialogResult = !string.IsNullOrWhiteSpace(SelectedType);
    }

    private void SourceType_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
