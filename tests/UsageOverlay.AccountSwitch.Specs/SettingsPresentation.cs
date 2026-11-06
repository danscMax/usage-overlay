using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsage.Core.Settings;
using UsageOverlay;
using UsageOverlay.Infrastructure;

internal static class SettingsPresentation
{
    internal static void Render(string directory)
    {
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var narrow in new[] { false, true })
        {
            ThemeManager.Instance.ApplyTheme(theme);
            var settings = new SettingsWindow(new OverlaySettings { Language = AppLanguage.Russian, Orientation = RailOrientation.Horizontal }, false, false,
                new ProviderSettingsStatus("Готов", "Подключено", "Выключено", false, "settings.json"), _ => Task.FromResult(""), () => { })
            { Width = narrow ? 460 : 650, Height = narrow ? 480 : 760 };
            settings.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)settings.Content;
            if (((TextBlock)settings.FindName("SettingsIntroText")).Text != "Лимиты ИИ и состояние проекта под рукой.")
                throw new InvalidOperationException("Settings header must use the translated product description");
            var credit = (TextBlock)settings.FindName("OriginalAuthorCredit");
            if (!new System.Windows.Documents.TextRange(credit.ContentStart, credit.ContentEnd).Text.Contains("Haroon", StringComparison.Ordinal)
                || !((TextBlock)settings.FindName("ForkVersionText")).Text.Contains("форк danscMax", StringComparison.Ordinal))
                throw new InvalidOperationException("Acknowledgements must retain original credit and identify this fork");
            var save = (Button)settings.FindName("SaveButton");
            var side = (RadioButton)settings.FindName("DetailsAboveRadio");
            foreach (var pair in new[] { ("VerticalRailRadio", "HorizontalRailRadio"), ("DetailsAboveRadio", "DetailsBelowRadio"), ("RightEdgeRadio", "LeftEdgeRadio") })
            {
                var first = (FrameworkElement)settings.FindName(pair.Item1);
                var second = (FrameworkElement)settings.FindName(pair.Item2);
                if (Math.Abs(first.TranslatePoint(new Point(0, 0), root).Y - second.TranslatePoint(new Point(0, 0), root).Y) > 0.5)
                    throw new InvalidOperationException("Position radio buttons must share the same baseline");
            }
            if (!side.IsEnabled) throw new InvalidOperationException("Horizontal layout must enable above/below preferences");
            ((RadioButton)settings.FindName("VerticalRailRadio")).IsChecked = true;
            if (side.IsEnabled) throw new InvalidOperationException("Vertical layout must explain automatic left/right placement");
            settings.UpdatePosition(new OverlaySettings { Placement = HorizontalPlacement.Custom, CustomXRatio = 0.2, CustomYRatio = 0.3 });
            if (((RadioButton)settings.FindName("CustomPositionRadio")).IsChecked != true) throw new InvalidOperationException("Dragging the bar must refresh the open position settings");
            ((TextBlock)settings.FindName("SavedStatusText")).Text = "Сохранено. Ориентация и положение применяются сразу; язык — после перезапуска.";
            root.UpdateLayout();
            var bottom = save.TranslatePoint(new Point(0, save.ActualHeight), root).Y;
            if (bottom > root.ActualHeight + 1 || save.TranslatePoint(new Point(save.ActualWidth, 0), root).X > root.ActualWidth + 1)
                throw new InvalidOperationException("Save button escaped the settings viewport");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, $"Settings-{theme}-{(narrow ? "Narrow" : "Normal")}.png"))) encoder.Save(file);
            var scroll = (ScrollViewer)settings.FindName("SettingsScrollViewer");
            scroll.ScrollToEnd();
            root.UpdateLayout();
            var creditsBitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            creditsBitmap.Render(root);
            var creditsEncoder = new PngBitmapEncoder();
            creditsEncoder.Frames.Add(BitmapFrame.Create(creditsBitmap));
            using (var file = File.Create(Path.Combine(directory, $"Credits-{theme}-{(narrow ? "Narrow" : "Normal")}.png"))) creditsEncoder.Save(file);
            settings.Close();
            Console.WriteLine($"PASS {theme} {(narrow ? "narrow" : "normal")} settings keep Save visible, update placement and disable incompatible controls");
        }
    }
}
