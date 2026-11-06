using System.Windows;
using UsageOverlay.Services;

internal static class BarPlacementSpecs
{
    internal static void Run()
    {
        var count = 0;
        foreach (var bounds in new[] { new WindowBounds(0, 0, 1200, 900, false), new WindowBounds(-1400, -200, -800, 500, false), new WindowBounds(0, 0, 316, 400, false) })
        foreach (var x in new[] { 0.05, 0.5, 0.95 })
        foreach (var y in new[] { 0.05, 0.5, 0.95 })
        foreach (var horizontal in new[] { true, false })
        foreach (var above in new[] { true, false })
        {
            var anchor = new Point(bounds.Left + (bounds.Right - bounds.Left) * x, bounds.Top + (bounds.Bottom - bounds.Top) * y);
            var bar = horizontal ? new Size(390, 54) : new Size(116, 198);
            var open = BarPlacement.Calculate(bounds, anchor, bar, new Size(460, 350), horizontal, above, true);
            var closed = BarPlacement.Calculate(bounds, anchor, bar, new Size(460, 350), horizontal, above, false);
            Check(open.Bar == closed.Bar, "Opening details cannot move the bar");
            Check(open.Window.Left >= bounds.Left + 7.9 && open.Window.Right <= bounds.Right - 7.9 && open.Window.Top >= bounds.Top + 7.9 && open.Window.Bottom <= bounds.Bottom - 7.9,
                "Panel stays within its visible host area");
            Check(!open.Bar.IntersectsWith(open.Details), "Details cannot overlap the bar or its hover button");
            count++;
        }
        var area = new WindowBounds(0, 0, 1200, 900, false);
        Check(BarPlacement.Calculate(area, new Point(600, 60), new Size(360, 54), new Size(460, 350), true, true, true).Above == false, "A panel near the top flips below");
        Check(BarPlacement.Calculate(area, new Point(600, 840), new Size(360, 54), new Size(460, 350), true, false, true).Above, "A panel near the bottom flips above");
        Check(!BarPlacement.Calculate(area, new Point(70, 500), new Size(116, 198), new Size(460, 350), false, true, true).Left, "A left-edge vertical dock opens to the right");
        Check(BarPlacement.Calculate(area, new Point(1130, 500), new Size(116, 198), new Size(460, 350), false, true, true).Left, "A right-edge vertical dock opens to the left");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2.25 })
        foreach (var horizontal in new[] { true, false })
        {
            var layout = BarPlacement.Calculate(area, new Point(1000, 700), new Size(96, 180), new Size(460, 350), horizontal, true, true);
            var dpi = new DpiScale(scale, scale);
            Point Physical(Point point) => new(point.X * scale, point.Y * scale);
            Check(BarPlacement.ContainsPhysicalPoint(layout, Physical(new Point(layout.Bar.Left + layout.Bar.Width / 2, layout.Bar.Top + layout.Bar.Height / 2)), dpi), "A physical pointer inside the bar stays inside at fractional DPI");
            Check(BarPlacement.ContainsPhysicalPoint(layout, Physical(new Point(layout.Details.Left + 10, layout.Details.Top + 10)), dpi), "A physical pointer inside details prevents false collapse");
            Check(!BarPlacement.ContainsPhysicalPoint(layout, Physical(new Point(layout.Window.Left - 2, layout.Window.Top - 2)), dpi), "A pointer outside both surfaces still closes details");
        }
        Console.WriteLine($"PASS {count} adaptive placement combinations preserve bounds and anchor");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
