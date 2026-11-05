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
        new(SourceTypes.Window, "A single window, such as Wizard101."),
        new(SourceTypes.Display, "An entire monitor. Use for exclusive fullscreen games."),
        new(SourceTypes.Camera, "Webcam or capture card."),
        new(SourceTypes.Browser, "A web page: chat, alerts or widgets."),
        new(SourceTypes.Image, "A PNG or JPG image."),
        new(SourceTypes.Text, "A text label."),
        new(SourceTypes.Microphone, "Microphone or line in."),
        new(SourceTypes.DesktopAudio, "Desktop audio."),
        new(SourceTypes.Folder, "Groups sources to show, hide, lock and move together."),
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
