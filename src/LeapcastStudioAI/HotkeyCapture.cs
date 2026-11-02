using System.Windows.Controls;
using System.Windows.Input;

namespace LeapcastStudioAI;

/// <summary>Turns a read-only TextBox into a "press the keys" hotkey field (Backspace / Delete / Esc clears it).</summary>
public static class HotkeyCapture
{
    public static TextBox Box(string? current)
    {
        var box = new TextBox { IsReadOnly = true, Text = HotkeyText.Normalize(current), ToolTip = "Click here, then press the keys (Backspace clears)" };
        box.PreviewKeyDown += (_, e) => Handle(box, e);
        return box;
    }

    public static void Handle(TextBox box, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key is Key.Back or Key.Delete or Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { box.Text = ""; return; }
        var m = Keyboard.Modifiers;
        var mods = HotkeyText.Mods.None;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyText.Mods.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyText.Mods.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyText.Mods.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyText.Mods.Win;
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..]; // D1 → 1
        box.Text = HotkeyText.Format(mods, name);
    }
}
