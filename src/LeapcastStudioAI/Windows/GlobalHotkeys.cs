using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace LeapcastStudioAI;

/// <summary>
/// System-wide hotkeys (RegisterHotKey): they fire while the game has focus, which is the whole point. A hotkey
/// another program already owns can't be registered; <see cref="Register"/> reports those.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _ids = new();
    private int _next = 0xB000;

    public event Action<string>? Pressed;

    public GlobalHotkeys(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _ids.TryGetValue(wParam.ToInt32(), out var text))
        {
            Pressed?.Invoke(text);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Replaces every registered hotkey with <paramref name="hotkeys"/>. Returns the ones that failed.</summary>
    public List<string> Register(IEnumerable<string> hotkeys)
    {
        Clear();
        var failed = new List<string>();
        foreach (var text in hotkeys)
        {
            if (HotkeyText.Parse(text) is not { } h || !TryKey(h.Key, out var key)) { failed.Add(text); continue; }
            int id = _next++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(_source.Handle, id, (uint)h.Mods | ModNoRepeat, vk)) _ids[id] = HotkeyText.Normalize(text);
            else failed.Add(text);
        }
        return failed;
    }

    private static bool TryKey(string name, out Key key)
    {
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name; // "1" → Key.D1
        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }

    private void Clear()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
    }

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(Hook);
    }
}
