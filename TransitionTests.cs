using System;
using System.Windows;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class TransitionTests
{
    public static void Run()
    {
        AssertEqual(new Rect(195, 0, 330, 60), ShelfGeometry.Silhouette(0), "compact silhouette");
        AssertEqual(new Rect(0, 0, 720, 460), ShelfGeometry.Silhouette(1), "expanded silhouette");
        AssertEqual(ShelfGeometry.Silhouette(0), ShelfGeometry.Silhouette(double.NaN), "NaN progress");
        Assert(ShelfGeometry.BodyOpacity(0) == 0 && ShelfGeometry.BodyOpacity(1) == 1,
            "body opacity endpoints");
        Assert(ShelfGeometry.CompactOpacity(0) == 1 && ShelfGeometry.CompactOpacity(1) == 0,
            "compact opacity endpoints");

        Rect work = new(100, 50, 1200, 900);
        Rect compact = ShelfGeometry.HostPlacement(work, 1.25, 24, false);
        Rect expanded = ShelfGeometry.HostPlacement(work, 1.25, 24, true);
        Assert(Math.Abs((compact.Left + compact.Width / 2) - (expanded.Left + expanded.Width / 2)) < 0.0001,
            "endpoint centers must match");
        Assert(Math.Abs(compact.Top - expanded.Top) < 0.0001, "endpoint top anchors must match");
        Assert(compact.Width == ShelfGeometry.CompactWidth * 1.25 + ShelfGeometry.Gutter * 2,
            "compact host width");
        Assert(expanded.Width == ShelfGeometry.ExpandedWidth * 1.25 + ShelfGeometry.Gutter * 2,
            "expanded host width");

        Rect shortCompact = ShelfGeometry.HostPlacement(new Rect(0, 0, 500, 220), 1, 200, false);
        Rect shortExpanded = ShelfGeometry.HostPlacement(new Rect(0, 0, 500, 220), 1, 200, true);
        Assert(shortCompact.Top == shortExpanded.Top, "short monitor anchor must not jump");
        Assert(shortExpanded.Width <= 500 && shortExpanded.Height <= 220, "oversized host must crop");

        using var controller = new ShelfTransitionController();
        int settled = 0;
        bool last = false;
        controller.Settled += expandedValue => { settled++; last = expandedValue; };
        controller.SetExpanded(true, false);
        Assert(controller.Progress == 1 && !controller.IsTransitioning && last, "immediate expansion");
        controller.SetExpanded(false, true);
        Assert(controller.TargetExpanded == false && controller.IsTransitioning, "animated collapse starts");
        controller.SetExpanded(true, true);
        Assert(controller.TargetExpanded, "animated reversal changes target");
        controller.Settle();
        Assert(controller.Progress == 1 && !controller.IsTransitioning && last,
            "settle commits only current endpoint");
        Assert(settled >= 2, "current endpoint must settle");
    }

    private static void AssertEqual(Rect expected, Rect actual, string message)
    {
        if (expected != actual) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
