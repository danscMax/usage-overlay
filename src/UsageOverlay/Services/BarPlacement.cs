using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsageOverlay.Services;

internal sealed record BarLayout(Rect Window, Rect Bar, Rect Details, bool Above, bool Left);

internal static class BarPlacement
{
    internal const double Gap = 6;
    internal const double Padding = 8;

    internal static bool ContainsPhysicalPoint(BarLayout layout, Point point, DpiScale dpi, bool includeDetails = true)
    {
        var logical = new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY);
        return layout.Bar.Contains(logical) || includeDetails && layout.Details.Contains(logical);
    }

    internal static BarLayout Calculate(WindowBounds bounds, Point anchor, Size bar, Size details, bool horizontal, bool preferAbove, bool expanded)
    {
        var area = new Rect(bounds.Left + Padding, bounds.Top + Padding,
            Math.Max(1, bounds.Right - bounds.Left - Padding * 2), Math.Max(1, bounds.Bottom - bounds.Top - Padding * 2));
        var width = Math.Min(bar.Width, area.Width);
        var height = Math.Min(bar.Height, area.Height);
        var meter = new Rect(Clamp(anchor.X - width / 2, area.Left, area.Right - width),
            Clamp(anchor.Y - height / 2, area.Top, area.Bottom - height), width, height);
        var aboveRoom = Math.Max(0, meter.Top - area.Top - Gap);
        var belowRoom = Math.Max(0, area.Bottom - meter.Bottom - Gap);
        var leftRoom = Math.Max(0, meter.Left - area.Left - Gap);
        var rightRoom = Math.Max(0, area.Right - meter.Right - Gap);
        var above = preferAbove ? aboveRoom >= details.Height || aboveRoom >= belowRoom : !(belowRoom >= details.Height || belowRoom >= aboveRoom);
        var left = leftRoom >= details.Width || leftRoom >= rightRoom;
        var popupWidth = Math.Min(details.Width, horizontal ? area.Width : Math.Max(leftRoom, rightRoom));
        var popupHeight = Math.Min(details.Height, horizontal ? above ? aboveRoom : belowRoom : area.Height);
        popupWidth = Math.Max(1, popupWidth);
        popupHeight = Math.Max(1, popupHeight);
        var popup = horizontal
            ? new Rect(Clamp(meter.Left + width / 2 - popupWidth / 2, area.Left, area.Right - popupWidth),
                above ? meter.Top - Gap - popupHeight : meter.Bottom + Gap, popupWidth, popupHeight)
            : new Rect(left ? meter.Left - Gap - popupWidth : meter.Right + Gap,
                Clamp(meter.Top + height / 2 - popupHeight / 2, area.Top, area.Bottom - popupHeight), popupWidth, popupHeight);
        var window = expanded ? Rect.Union(meter, popup) : meter;
        return new(window, meter, popup, above, left);
    }

    private static double Clamp(double value, double minimum, double maximum) => Math.Clamp(value, minimum, Math.Max(minimum, maximum));
}
