using System.Windows;
using System.Windows.Input;

namespace LeapcastStudioAI;

public partial class SourcePickerWindow : Window
{
    public sealed record Choice(string Title, string Description);

    private static readonly Choice[] Choices =
    {
        new(SourceTypes.Display, "Your whole screen via GPU capture. Best for Wizard101, including fullscreen."),
        new(SourceTypes.Window, "One window (e.g. Wizard101 in windowed mode) via GDI. Useful with several monitors."),
        new(SourceTypes.Camera, "Webcam overlay, denoised before encoding to save bandwidth."),
        new(SourceTypes.Image, "A PNG/JPG overlay: frame, logo, \"starting soon\" screen."),
        new(SourceTypes.Text, "A text label rendered in Segoe UI with a drop shadow."),
        new(SourceTypes.Microphone, "Your mic, with adaptive background-noise removal."),
        new(SourceTypes.DesktopAudio, "Everything you hear: game sound, music, alerts."),
    };

    public string? SelectedType { get; private set; }

    public SourcePickerWindow()
    {
        InitializeComponent();
        SourceType.ItemsSource = Choices;
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
