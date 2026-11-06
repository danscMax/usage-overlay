using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using CodexUsage.Core.Settings;
using UsageOverlay;
using UsageOverlay.Infrastructure;
using UsageOverlay.Services;

internal static class PopupStabilitySpecs
{
    internal static void Run(MainWindow window, string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var orientation in new[] { RailOrientation.Horizontal, RailOrientation.Vertical })
        {
            ThemeManager.Instance.ApplyTheme(theme);
            typeof(MainWindow).GetMethod("CloseUsageDetails", flags)!.Invoke(window, null);
            typeof(MainWindow).GetField("_settings", flags)!.SetValue(window, new OverlaySettings
            {
                Language = AppLanguage.Russian, CodexEnabled = true, ClaudeEnabled = true, ShowOtherApps = true,
                PrimaryDisplay = PrimaryUsageDisplay.Used, AnimationsEnabled = true, Orientation = orientation,
                Placement = HorizontalPlacement.Custom, CustomXRatio = 0.7, CustomYRatio = 0.6
            });
            typeof(MainWindow).GetField("_displayedProvider", flags)!.SetValue(window, CodexUsage.Core.Providers.UsageProviderKind.Codex);
            typeof(MainWindow).GetField("_lastBounds", flags)!.SetValue(window, new WindowBounds(0, 0, 1200, 900, false));
            var render = typeof(MainWindow).GetMethod("Render", flags)!;
            render.Invoke(window, null);
            Pump(100);
            var panel = (FrameworkElement)window.FindName("DetailsPanel");
            var root = (FrameworkElement)window.Content;
            var rows = (StackPanel)window.FindName("SessionMetricsPanel");
            var originalRow = rows.Children[0];
            var pills = (WrapPanel)window.FindName("MicroLimitsPanel");
            var originalPill = pills.Children[0];
            var nativeChanges = 0;
            IntPtr Observe(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == 0x0047)
                {
                    var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                    if ((position.Flags & 3) != 3) nativeChanges++;
                }
                return IntPtr.Zero;
            }
            source.AddHook(Observe);
            try
            {
                typeof(MainWindow).GetMethod("ExpandDetails", flags)!.Invoke(window, null);
                // Exercise the running fade even when Windows animations are disabled on this machine.
                panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                var previousOpacity = 0d;
                for (var index = 0; index < 12; index++)
                {
                    root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    var session = (SessionSnapshot)typeof(MainWindow).GetField("_session", flags)!.GetValue(window)!;
                    typeof(MainWindow).GetField("_session", flags)!.SetValue(window, session with { Duration = TimeSpan.FromSeconds(510 + index) });
                    render.Invoke(window, null);
                    Pump(70);
                    if (panel.Visibility != Visibility.Visible || panel.Opacity + 0.01 < previousOpacity)
                        throw new InvalidOperationException("Opening opacity must be monotonic while readings and mouse-enter events arrive");
                    previousOpacity = panel.Opacity;
                    if (!ReferenceEquals(originalRow, rows.Children[0]) || !ReferenceEquals(originalPill, pills.Children[0]))
                        throw new InvalidOperationException("Metric updates must retain their visual elements");
                }
                if (panel.Opacity < 0.99 || nativeChanges != 1)
                    throw new InvalidOperationException($"Popup must open with one native bounds change and remain stable; observed {nativeChanges}");
                File.WriteAllText(Path.Combine(directory, $"Stability-{theme}-{orientation}.json"), JsonSerializer.Serialize(new
                { Theme = theme.ToString(), Orientation = orientation.ToString(), NativeBoundsChanges = nativeChanges, Samples = 12,
                    AnimationsEnabled = true, ForcedOpacityAnimation = true, SystemAnimations = SystemParameters.ClientAreaAnimation, FinalOpacity = panel.Opacity, ReusedElements = true }));
                Console.WriteLine($"PASS {theme} {orientation}: one native resize, stable opacity and reused rows during 12 live updates");
            }
            finally { source.RemoveHook(Observe); }
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
