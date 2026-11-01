using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LeapcastStudioAI;

/// <summary>
/// The "+" in Sources: a source already set up in another scene (added as a copy with all its settings), or a new
/// source of any type.
/// </summary>
public partial class SourcePickerWindow : Window
{
    public sealed record Choice(string Title, string Description);
    public sealed record ExistingChoice(string Title, string Description, StudioSource Source);

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
    /// <summary>Set when the user picked a source from another scene (the caller adds a copy of it).</summary>
    public StudioSource? SelectedExisting { get; private set; }

    /// <param name="scenes">Every scene; sources of <paramref name="target"/> itself aren't offered.</param>
    public SourcePickerWindow(IEnumerable<StudioScene>? scenes = null, StudioScene? target = null)
    {
        InitializeComponent();
        // every pickable type must be listed here (keeps the picker in step with SourceTypes.Pickable)
        SourceType.ItemsSource = Choices.OrderBy(c => Array.IndexOf(SourceTypes.Pickable, c.Title)).ToArray();

        var existing = SourceLibrary.FromOtherScenes(scenes ?? Array.Empty<StudioScene>(), target)
            .Select(x => new ExistingChoice(x.Source.Name, $"{x.Source.Type} · in {x.Scene}", x.Source)).ToList();
        ExistingList.ItemsSource = existing;
        if (existing.Count == 0)
        {
            ExistingGroup.Visibility = Visibility.Collapsed;
            SourceType.SelectedIndex = 0;
        }
        else
        {
            NewHeader.Visibility = Visibility.Collapsed;
            ExistingList.SelectedIndex = 0;
        }
    }

    private bool _syncing;

    private void ExistingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ExistingList.SelectedItem is null) return;
        _syncing = true; SourceType.SelectedItem = null; _syncing = false;
    }

    private void SourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SourceType.SelectedItem is null) return;
        _syncing = true; ExistingList.SelectedItem = null; _syncing = false;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedExisting = (ExistingList.SelectedItem as ExistingChoice)?.Source;
        SelectedType = SelectedExisting is null ? (SourceType.SelectedItem as Choice)?.Title : null;
        DialogResult = SelectedExisting is not null || !string.IsNullOrWhiteSpace(SelectedType);
    }

    private void SourceType_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
