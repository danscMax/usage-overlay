using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UsageOverlay.Services;

public static class NativeWindowStyle
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivateStyle = 0x08000000L;
    private const long ToolWindowStyle = 0x00000080L;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    public static void ApplyNonActivatingToolWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var current = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            ExtendedStyleIndex,
            new IntPtr(current | NoActivateStyle | ToolWindowStyle));
    }

    public static void ApplyDarkTitleBar(Window window)
    {
        ApplyTitleBarTheme(window, isDark: true);
    }

    public static void ApplyTitleBarTheme(Window window, bool isDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = isDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }

    internal static bool SetBounds(IntPtr window, Rect bounds, DpiScale dpi)
    {
        var left = (int)Math.Round(bounds.Left * dpi.DpiScaleX);
        var top = (int)Math.Round(bounds.Top * dpi.DpiScaleY);
        var width = (int)Math.Round(bounds.Width * dpi.DpiScaleX);
        var height = (int)Math.Round(bounds.Height * dpi.DpiScaleY);
        if (GetWindowRect(window, out var current) && current.Left == left && current.Top == top
            && current.Right - current.Left == width && current.Bottom - current.Top == height) return true;
        // Commit size and position together without activating or changing the window order.
        return SetWindowPos(window, IntPtr.Zero, left, top, width, height, 0x0214);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}
