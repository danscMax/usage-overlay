using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexUsage.Core.Settings;
using UsageOverlay;
using UsageOverlay.Infrastructure;

internal static class HoverCollapseSpecs
{
    internal static void Run(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var render = typeof(MainWindow).GetMethod("Render", flags)!;
        var expand = typeof(MainWindow).GetMethod("ExpandDetails", flags)!;
        var close = typeof(MainWindow).GetMethod("CloseUsageDetails", flags)!;
        var isExpanded = typeof(MainWindow).GetField("_isExpanded", flags)!;
        var isPinned = typeof(MainWindow).GetField("_isPinned", flags)!;
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_hoverTimer", flags)!.GetValue(window)!;
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var orientation in new[] { RailOrientation.Horizontal, RailOrientation.Vertical })
        {
            ThemeManager.Instance.ApplyTheme(theme);
            close.Invoke(window, null);
            typeof(MainWindow).GetField("_settings", flags)!.SetValue(window, new OverlaySettings
            { Language = AppLanguage.Russian, Orientation = orientation, AnimationsEnabled = true });
            render.Invoke(window, null);
            var button = (Button)window.FindName(orientation == RailOrientation.Horizontal ? "ExpandMicroButton" : "VerticalExpandButton");
            MouseEvent(button, Mouse.MouseLeaveEvent);
            expand.Invoke(window, null);
            if (!(bool)isExpanded.GetValue(window)! || (bool)isPinned.GetValue(window)!) throw new InvalidOperationException("Hover scenario must start open and unpinned");
            var layoutField = typeof(MainWindow).GetField("_barLayout", flags)!;
            var realLayout = layoutField.GetValue(window);
            var pointer = System.Windows.Forms.Cursor.Position;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var underPointer = new Rect(pointer.X / dpi.DpiScaleX - 100, pointer.Y / dpi.DpiScaleY - 100, 200, 200);
            try
            {
                layoutField.SetValue(window, new UsageOverlay.Services.BarLayout(underPointer, underPointer, underPointer, true, true));
                ((FrameworkElement)window.Content).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                typeof(MainWindow).GetMethod("CollapseTimer_OnTick", flags)!.Invoke(window, [null, EventArgs.Empty]);
                if (!(bool)isExpanded.GetValue(window)! || (bool)typeof(MainWindow).GetField("_detailsClosing", flags)!.GetValue(window)!)
                    throw new InvalidOperationException("A transient MouseLeave during native resizing must not close or fade details while the physical pointer remains inside");
            }
            finally { layoutField.SetValue(window, realLayout); }
            timer.Start();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if ((bool)isExpanded.GetValue(window)! || (bool)isPinned.GetValue(window)! || timer.IsEnabled)
                throw new InvalidOperationException("One click must close a hover-opened panel and cancel pending hover work");
            realLayout = layoutField.GetValue(window);
            try
            {
                layoutField.SetValue(window, new UsageOverlay.Services.BarLayout(underPointer, underPointer, underPointer, true, true));
                MouseEvent(button, Mouse.MouseLeaveEvent);
                MouseEvent(button, Mouse.MouseEnterEvent);
                if (timer.IsEnabled) throw new InvalidOperationException("A resize-induced MouseLeave cannot rearm hover under a stationary pointer after manual close");
            }
            finally { layoutField.SetValue(window, realLayout); }
            MouseEvent(button, Mouse.MouseEnterEvent);
            render.Invoke(window, null);
            if (timer.IsEnabled || (bool)isExpanded.GetValue(window)!) throw new InvalidOperationException("A dismissed panel must not reopen under a stationary pointer");
            MouseEvent(button, Mouse.MouseLeaveEvent);
            MouseEvent(button, Mouse.MouseEnterEvent);
            if (!timer.IsEnabled) throw new InvalidOperationException("Leaving and returning must allow hover again");
            MouseEvent(button, Mouse.MouseLeaveEvent);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!(bool)isExpanded.GetValue(window)! || !(bool)isPinned.GetValue(window)!) throw new InvalidOperationException("A click on a closed panel must still open and pin it");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if ((bool)isExpanded.GetValue(window)! || (bool)isPinned.GetValue(window)!) throw new InvalidOperationException("A pinned panel must also close in one click");
            MouseEvent(button, Mouse.MouseLeaveEvent);
            Console.WriteLine($"PASS {theme} {orientation}: hover-opened and pinned panels close in one click without immediate reopening");
        }
    }

    private static void MouseEvent(UIElement element, RoutedEvent routedEvent) => element.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routedEvent });
}
