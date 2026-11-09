using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LeapcastStudioAI;

/// <summary>
/// The "+" in Sources. Step 1: the kind of source. Step 2 (when you already have sources of that kind, in any
/// scene, this one included): make a new one, or add one you already have (a copy with every setting).
/// </summary>
public partial class SourcePickerWindow : Window
{
    public sealed record Choice(string Title, string Description, string Have = "");
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

    private readonly IReadOnlyList<StudioScene> _scenes;
    private readonly StudioScene? _target;

    public string? SelectedType { get; private set; }
    /// <summary>Set when the user picked a source they already have (the caller adds a copy of it).</summary>
    public StudioSource? SelectedExisting { get; private set; }
    /// <summary>The name typed for a new source (null: the usual name for its type).</summary>
    public string? NewSourceName { get; private set; }

    /// <param name="scenes">Every scene (sources already in them are offered, the target scene's too).</param>
    public SourcePickerWindow(IEnumerable<StudioScene>? scenes = null, StudioScene? target = null)
    {
        InitializeComponent();
        _scenes = (scenes ?? Array.Empty<StudioScene>()).ToList();
        _target = target;
        // every pickable type must be listed here (keeps the picker in step with SourceTypes.Pickable)
        SourceType.ItemsSource = Choices.OrderBy(c => Array.IndexOf(SourceTypes.Pickable, c.Title))
            .Select(c =>
            {
                int n = SourceLibrary.Existing(_scenes, c.Title, _target).Count;
                return c with { Have = n == 0 ? "" : n == 1 ? "· you have 1" : $"· you have {n}" };
            }).ToArray();
        SourceType.SelectedIndex = 0;
    }

    /// <summary>The usual name of a new source of this type.</summary>
    public static string DefaultName(string type) => type switch
    {
        SourceTypes.Camera => "Webcam",
        SourceTypes.Window => "Wizard101 window",
        SourceTypes.DesktopAudio => "Desktop audio",
        SourceTypes.Microphone => "Microphone",
        SourceTypes.Browser => "Chat",
        _ => type,
    };

    private bool OnChoicePage => ChoicePage.Visibility == Visibility.Visible;

    private void ShowChoices(string type)
    {
        var existing = SourceLibrary.Existing(_scenes, type, _target)
            .Select(x => new ExistingChoice(x.Source.Name, Where(x.Scenes), x.Source)).ToList();
        if (existing.Count == 0) { Finish(type, null); return; }   // nothing to reuse: straight to setting it up
        SelectedType = type;
        ChoiceHeader.Text = "Add " + type;
        NewName.Text = UniqueName(DefaultName(type), type);
        ExistingList.ItemsSource = existing;
        ExistingList.SelectedItem = null;
        NewRadio.IsChecked = true;
        ExistingRadio.IsChecked = false;
        TypePage.Visibility = Visibility.Collapsed;
        ChoicePage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        AddButton.Content = "Add source";
        NewName.Focus();
        NewName.SelectAll();
    }

    private string Where(List<string> scenes)
    {
        bool here = _target is not null && scenes.Contains(_target.Name);
        var others = scenes.Where(s => _target is null || s != _target.Name).ToList();
        if (here && others.Count == 0) return "in this scene";
        string list = others.Count <= 3 ? string.Join(", ", others) : $"{string.Join(", ", others.Take(3))} and {others.Count - 3} more";
        return (here ? "in this scene and " : "in ") + list;
    }

    /// <summary>"Webcam", or "Webcam 2" when there's already a Webcam.</summary>
    private string UniqueName(string name, string type)
    {
        var taken = _scenes.SelectMany(sc => sc.Sources).Where(x => x.Type == type).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    private void Finish(string? type, StudioSource? existing)
    {
        SelectedExisting = existing;
        SelectedType = existing is null ? type : null;
        DialogResult = existing is not null || !string.IsNullOrWhiteSpace(SelectedType);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!OnChoicePage)
        {
            if (SourceType.SelectedItem is Choice c) ShowChoices(c.Title);
            return;
        }
        if (ExistingRadio.IsChecked == true)
        {
            if (ExistingList.SelectedItem is ExistingChoice x) Finish(null, x.Source);
            else MessageBox.Show(this, "Pick one from the list, or choose \"Make a new one\".", "Add source", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = NewName.Text.Trim();
        NewSourceName = name.Length > 0 ? name : null;
        Finish(SelectedType, null);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ChoicePage.Visibility = Visibility.Collapsed;
        TypePage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        AddButton.Content = "Next";
        SelectedType = null;
    }

    private bool _syncing;

    private void ExistingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ExistingList.SelectedItem is null) return;
        _syncing = true; ExistingRadio.IsChecked = true; NewRadio.IsChecked = false; _syncing = false;
    }

    private void ExistingRadio_Click(object sender, RoutedEventArgs e)
    {
        if (ExistingList.SelectedItem is null && ExistingList.Items.Count > 0) ExistingList.SelectedIndex = 0;
    }

    private void NewRadio_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true; ExistingList.SelectedItem = null; _syncing = false;
    }

    private void NewName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        _syncing = true; NewRadio.IsChecked = true; ExistingRadio.IsChecked = false; ExistingList.SelectedItem = null; _syncing = false;
    }

    private void SourceType_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void SourceType_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add_Click(sender, e);

    private void ExistingList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExistingList.SelectedItem is ExistingChoice x) Finish(null, x.Source);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
