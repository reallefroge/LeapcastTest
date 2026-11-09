using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LeapcastStudioAI;

/// <summary>Windows/WPF services the engine needs: text overlay rendering and monitor geometry.</summary>
public sealed class WindowsPlatform : IPlatformServices
{
    private readonly string _overlayDir = Path.Combine(StateStore.Folder, "cache", "overlays");

    public IFrameSource? OpenBrowser(StudioSource source) =>
        Application.Current is { } app ? WebViewFrameSource.Create(source, app.Dispatcher) : null;

    /// <summary>Scenes layered on the graphics card (Direct3D 11); null = the classic compositor is used.</summary>
    public IGpuCanvas? CreateGpuCanvas(int width, int height, int fps, Func<string, GpuFrame?> frames) =>
        new D3DCanvas(width, height, fps, frames);

    public string? RenderText(StudioSource source, int outputHeight)
    {
        if (string.IsNullOrWhiteSpace(source.Text)) return null;
        Directory.CreateDirectory(_overlayDir);
        foreach (var old in Directory.GetFiles(_overlayDir, $"text_{source.Id:N}_*.png"))
        {
            try { File.Delete(old); } catch { }
        }
        string path = Path.Combine(_overlayDir, $"text_{source.Id:N}_{DateTime.UtcNow.Ticks}.png");

        // WPF rendering must happen on the UI thread.
        Application.Current.Dispatcher.Invoke(() =>
        {
            double em = Math.Clamp(source.FontSize, 8, 300) * outputHeight / 1080.0;
            Brush brush;
            try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(source.TextColor)); }
            catch { brush = Brushes.White; }
            var ft = new FormattedText(source.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                em, brush, 1.0);
            var shadow = new FormattedText(source.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                em, new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 1.0);
            double pad = Math.Ceiling(em * 0.15);
            int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace + pad * 2);
            int h = (int)Math.Ceiling(ft.Height + pad * 2);
            w += w % 2; h += h % 2;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double off = Math.Max(1, em / 18);
                dc.DrawText(shadow, new Point(pad + off, pad + off)); // soft drop shadow keeps text readable over the game
                dc.DrawText(ft, new Point(pad, pad));
            }
            var bmp = new RenderTargetBitmap(Math.Max(2, w), Math.Max(2, h), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        });
        return path;
    }

    /// <summary>Size of a window's client area in physical pixels (what GPU window capture delivers).</summary>
    public (int W, int H)? WindowClientSize(long hwnd)
    {
        if (hwnd == 0 || !GetClientRect(new IntPtr(hwnd), out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        return w > 16 && h > 16 ? (w, h) : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CLIENTRECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out CLIENTRECT rect);

    public long? FindWindow(string title)
    {
        long? found = null;
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, lParam) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (string.Equals(sb.ToString().Trim(), title, StringComparison.Ordinal)) { found = h.ToInt64(); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public (int X, int Y, int W, int H)? MonitorBounds(int index)
    {
        var mons = Monitors();
        return index >= 0 && index < mons.Count ? mons[index] : null;
    }

    /// <summary>Monitors in physical pixels, primary first (matches desktop-duplication output order on most PCs).</summary>
    public static List<(int X, int Y, int W, int H)> Monitors()
    {
        var list = new List<(int, int, int, int, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add((mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Item5).ThenBy(m => m.Item1).Select(m => (m.Item1, m.Item2, m.Item3, m.Item4)).ToList();
    }

    /// <summary>Titles of visible top-level windows (for picking the Wizard101 window).</summary>
    public static List<string> WindowTitles()
    {
        var titles = new List<string>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, lParam) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString().Trim();
            if (t.Length > 0 && t != "Program Manager" && !titles.Contains(t)) titles.Add(t);
            return true;
        }, IntPtr.Zero);
        // Put the game first if it's open.
        return titles.OrderByDescending(t => t.Contains("Wizard101", StringComparison.OrdinalIgnoreCase)).ThenBy(t => t).ToList();
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

/// <summary>Stream keys are encrypted with Windows DPAPI for the current user, never stored as plain text.</summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LeapcastStudioAI.stream-key.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}
