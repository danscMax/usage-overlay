using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexUsage.Core.Settings;
using UsageOverlay;
using UsageOverlay.Infrastructure;
using UsageOverlay.Services;

internal static class QuickLayoutSpecs
{
    internal static void Run(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var render = typeof(MainWindow).GetMethod("Render", flags)!;
        var close = typeof(MainWindow).GetMethod("CloseUsageDetails", flags)!;
        var expand = typeof(MainWindow).GetMethod("ExpandDetails", flags)!;
        var horizontal = (RadioButton)window.FindName("QuickHorizontalLayout");
        var vertical = (RadioButton)window.FindName("QuickVerticalLayout");
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeManager.Instance.ApplyTheme(theme);
            close.Invoke(window, null);
            typeof(MainWindow).GetField("_settings", flags)!.SetValue(window, new OverlaySettings
            {
                Language = AppLanguage.Russian, Orientation = RailOrientation.Horizontal, AnimationsEnabled = false,
                Placement = HorizontalPlacement.Custom, CustomXRatio = 0.65, CustomYRatio = 0.6
            });
            render.Invoke(window, null);
            Pump();
            var details = (FrameworkElement)window.FindName("DetailsPanel");
            foreach (var orientation in new[] { RailOrientation.Horizontal, RailOrientation.Vertical })
            {
                var bar = (FrameworkElement)window.FindName(orientation == RailOrientation.Horizontal ? "HorizontalHitTarget" : "RailHitTarget");
                var gear = (Button)window.FindName(orientation == RailOrientation.Horizontal ? "SettingsButton" : "VerticalSettingsButton");
                close.Invoke(window, null);
                Pump();
                Check(gear.IsVisible && bar.IsAncestorOf(gear) && !details.IsAncestorOf(gear), "Settings gear remains on the collapsed bar");
                gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var settingsWindow = (SettingsWindow)typeof(MainWindow).GetField("_settingsWindow", flags)!.GetValue(window)!;
                Check(settingsWindow.IsVisible, "Bar gear opens settings");
                expand.Invoke(window, null);
                (orientation == RailOrientation.Horizontal ? vertical : horizontal).IsChecked = true;
                Pump();
                var expected = orientation == RailOrientation.Horizontal ? RailOrientation.Vertical : RailOrientation.Horizontal;
                var updated = (OverlaySettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
                Check(updated.Orientation == expected && updated.CustomXRatio == 0.65 && updated.CustomYRatio == 0.6,
                    "Quick switch changes orientation without overwriting position");
                Check(horizontal.IsChecked == (expected == RailOrientation.Horizontal) && vertical.IsChecked == (expected == RailOrientation.Vertical),
                    "Quick switch highlights the current orientation");
                Check((bool)typeof(MainWindow).GetField("_isPinned", flags)!.GetValue(window)! && details.Visibility == Visibility.Visible,
                    "Quick switch keeps the details card open and pinned");
                Check(((RadioButton)settingsWindow.FindName("HorizontalRailRadio")).IsChecked == (expected == RailOrientation.Horizontal),
                    "An open settings window follows the quick switch");
                var modePanel = (FrameworkElement)window.FindName("QuickLayoutPanel");
                Check(modePanel.ActualWidth <= 43 && modePanel.ActualHeight <= 21
                    && !((FrameworkElement)window.FindName("DetailsGroups")).IsAncestorOf(modePanel),
                    "Orientation switch is a tiny header control without a separate content row");
                if (expected == RailOrientation.Vertical)
                {
                    var reveal = (Button)window.FindName("VerticalExpandButton");
                    Check(reveal.ActualWidth >= 48 && reveal.InputHitTest(new Point(4, reveal.ActualHeight / 2)) is not null,
                        "Vertical reveal uses the whole former status area");
                    Check(bar.ActualWidth <= 106 && !((TextBlock)window.FindName("VerticalFreeText")).Text.Contains('%')
                        && !((TextBlock)window.FindName("VerticalQuotaText")).Text.Contains('·'),
                        "Vertical dock fits its contents without a duplicate free percentage or middle-dot separators");
                    Check(reveal.Content is System.Windows.Shapes.Path { StrokeLineJoin: System.Windows.Media.PenLineJoin.Round },
                        "Reveal is a rounded vector chevron");
                    var noticeField = typeof(MainWindow).GetField("_environmentNotices", flags)!;
                    var sessionField = typeof(MainWindow).GetField("_session", flags)!;
                    var notices = noticeField.GetValue(window);
                    var session = (SessionSnapshot)sessionField.GetValue(window)!;
                    noticeField.SetValue(window, Array.Empty<EnvironmentNotice>());
                    sessionField.SetValue(window, session with { Action = "Idle" });
                    render.Invoke(window, null);
                    Check(((TextBlock)window.FindName("VerticalDiagnosticsText")).Visibility == Visibility.Collapsed,
                        "Idle or completed status does not occupy the reveal area");
                    noticeField.SetValue(window, notices);
                    sessionField.SetValue(window, session);
                    render.Invoke(window, null);
                }
                settingsWindow.Close();
            }
            Console.WriteLine($"PASS {theme}: both bar gears open settings and both quick layout directions work");
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
