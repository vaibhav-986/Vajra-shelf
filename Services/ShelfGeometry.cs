using System;
using System.Windows;

namespace VajraShelf.Services;

/// <summary>Pure DIP geometry shared by the shelf reveal and native endpoint placement.</summary>
public static class ShelfGeometry
{
    public const double CompactWidth = 330;
    public const double CompactHeight = 60;
    public const double ExpandedWidth = 720;
    public const double ExpandedHeight = 460;
    public const double Gutter = 12;

    public static double NormalizeProgress(double progress) =>
        double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;

    public static Rect Silhouette(double progress)
    {
        double p = NormalizeProgress(progress);
        double width = CompactWidth + (ExpandedWidth - CompactWidth) * p;
        double height = CompactHeight + (ExpandedHeight - CompactHeight) * p;
        return new Rect((ExpandedWidth - width) / 2, 0, width, height);
    }

    public static double CornerRadius(double progress) => 30 - 10 * NormalizeProgress(progress);
    public static double BodyOpacity(double progress) => NormalizeProgress((NormalizeProgress(progress) - 0.10) / 0.72);
    public static double BodyOffset(double progress) => 12 * (1 - NormalizeProgress(progress));
    public static double CompactOpacity(double progress) => 1 - NormalizeProgress((NormalizeProgress(progress) - 0.18) / 0.42);

    public static TimeSpan TransitionDuration(double fromProgress, bool expanded)
    {
        double distance = Math.Abs((expanded ? 1 : 0) - NormalizeProgress(fromProgress));
        return TimeSpan.FromMilliseconds((expanded ? 330 : 260) * distance);
    }

    /// <summary>
    /// Endpoint hosts share a center and top anchor. The expanded height is used
    /// while clamping the offset so changing endpoints cannot move the shelf.
    /// </summary>
    public static Rect HostPlacement(Rect workArea, double scale, double topOffset, bool expandedHost)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.X) || !double.IsFinite(workArea.Y) ||
            !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            workArea.Width < 1 || workArea.Height < 1)
            workArea = new Rect(0, 0, 1920, 1080);

        scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.8, 1.5) : 1;
        topOffset = double.IsFinite(topOffset) ? Math.Clamp(topOffset, 0, 200) : 82;
        double expandedHeight = Math.Min(workArea.Height, ExpandedHeight * scale + Gutter * 2);
        double width = Math.Min(workArea.Width,
            (expandedHost ? ExpandedWidth : CompactWidth) * scale + Gutter * 2);
        double height = Math.Min(workArea.Height,
            (expandedHost ? ExpandedHeight : CompactHeight) * scale + Gutter * 2);
        double top = workArea.Top + Math.Min(topOffset, Math.Max(0, workArea.Height - expandedHeight));
        return new Rect(workArea.Left + (workArea.Width - width) / 2, top, width, height);
    }
}
