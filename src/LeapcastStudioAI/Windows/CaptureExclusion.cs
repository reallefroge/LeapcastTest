using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LeapcastStudioAI;

/// <summary>
/// Settings → "Hide Leapcast from display capture": every Leapcast window (the studio, its dialogs and popped-out
/// panels) is left out of screen captures, so a display capture shows what's behind them. Windows 10 2004 or newer.
/// </summary>
public static class CaptureExclusion
{
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private static bool _enabled, _hooked;

    /// <summary>Browser sources render in hidden helper windows: those must stay capturable.</summary>
    private static bool IsHelper(Window w) => w.Title == "Leapcast browser source";

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!_hooked)
            {
                _hooked = true;
                // windows opened later (dialogs, popped-out panels) follow the setting too
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));
            }
            if (Application.Current is { } app) foreach (Window w in app.Windows) Apply(w);
        }
    }

    public static void Apply(Window w)
    {
        if (IsHelper(w)) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowDisplayAffinity(hwnd, _enabled ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); } catch { }
    }
}
