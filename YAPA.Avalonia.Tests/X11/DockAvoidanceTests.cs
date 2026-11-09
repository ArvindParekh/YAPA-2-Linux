using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

/// <summary>
/// Geometry fixtures mirror the reference desktop: eDP-1 1920x1080+0+0
/// (primary, GNOME top bar and Ubuntu Dock) and HDMI-1 1920x1080+1920+0.
/// The top bar and dock rects are the values read live from Mutter's stage
/// input shape.
/// </summary>
internal static class Desk
{
    public static readonly PixelRect Edp = new(0, 0, 1920, 1080);
    public static readonly PixelRect Hdmi = new(1920, 0, 1920, 1080);
    public static readonly IReadOnlyList<PixelRect> Monitors = new[] { Edp, Hdmi };

    public static readonly PixelRect TopBar = new(0, 0, 1920, 40);
    public static readonly PixelRect Dock = new(96, 999, 1728, 81);
    public static readonly PixelRect LeftDock = new(0, 40, 70, 1040);
}

[TestClass]
public class ChromeClassificationTests
{
    private static ChromeZone? Classify(PixelRect r) => DockAvoidance.Classify(r, Desk.Monitors);

    [TestMethod]
    public void RealTopBarIsTopChromeOnPrimary()
    {
        var zone = Classify(Desk.TopBar);
        Assert.IsNotNull(zone);
        Assert.AreEqual(ScreenEdge.Top, zone.Value.Edge);
        Assert.AreEqual(Desk.Edp, zone.Value.Monitor);
        Assert.AreEqual(40, zone.Value.Thickness);
        Assert.AreEqual(1920, zone.Value.Length);
    }

    [TestMethod]
    public void RealDockContainerIsBottomChrome()
    {
        var zone = Classify(Desk.Dock);
        Assert.IsNotNull(zone);
        Assert.AreEqual(ScreenEdge.Bottom, zone.Value.Edge);
        Assert.AreEqual(Desk.Edp, zone.Value.Monitor);
        Assert.AreEqual(81, zone.Value.Thickness);
    }

    [TestMethod]
    public void LeftAndRightDocksClassifyByEdge()
    {
        Assert.AreEqual(ScreenEdge.Left, Classify(Desk.LeftDock)?.Edge);

        var rightOnHdmi = new PixelRect(3840 - 64, 100, 64, 800);
        var zone = Classify(rightOnHdmi);
        Assert.AreEqual(ScreenEdge.Right, zone?.Edge);
        Assert.AreEqual(Desk.Hdmi, zone?.Monitor);
    }

    [TestMethod]
    public void BottomDockOnSecondaryMonitorBelongsToIt()
    {
        var zone = Classify(new PixelRect(2200, 1010, 1300, 70));
        Assert.AreEqual(ScreenEdge.Bottom, zone?.Edge);
        Assert.AreEqual(Desk.Hdmi, zone?.Monitor);
    }

    [DataTestMethod]
    [DataRow(0, 0, 1920, 1080, DisplayName = "overview / fullscreen shield")]
    [DataRow(0, 0, 3840, 1080, DisplayName = "unshaped stage spanning both monitors")]
    [DataRow(710, 40, 500, 400, DisplayName = "calendar popup under the top bar")]
    [DataRow(660, 40, 600, 100, DisplayName = "notification banner")]
    [DataRow(300, 700, 250, 290, DisplayName = "dock right-click menu above the dock")]
    [DataRow(900, 300, 120, 120, DisplayName = "floating OSD")]
    [DataRow(0, 0, 1, 1, DisplayName = "hot corner pixel")]
    public void TransientShellUiIsNotChrome(int x, int y, int w, int h)
        => Assert.IsNull(Classify(new PixelRect(x, y, w, h)));

    [TestMethod]
    public void ThicknessCeilingIsTwoHundredPixels()
    {
        Assert.IsNotNull(Classify(new PixelRect(100, 1080 - 200, 1000, 200)));
        Assert.IsNull(Classify(new PixelRect(100, 1080 - 201, 1000, 201)));
    }

    [TestMethod]
    public void ThicknessCeilingIsQuarterOfMonitorOnSmallScreens()
    {
        var small = new PixelRect(0, 0, 1024, 600);
        var monitors = new[] { small };
        // Vertical: min(200, 600 * 0.25 = 150).
        Assert.IsNotNull(DockAvoidance.Classify(new PixelRect(0, 450, 1024, 150), monitors));
        Assert.IsNull(DockAvoidance.Classify(new PixelRect(0, 449, 1024, 151), monitors));
        // Horizontal: min(200, 1024 * 0.25 = 256), so the absolute cap rules.
        Assert.IsNotNull(DockAvoidance.Classify(new PixelRect(0, 0, 200, 600), monitors));
        Assert.IsNull(DockAvoidance.Classify(new PixelRect(0, 0, 201, 600), monitors));
    }

    [TestMethod]
    public void MinimumLengthIsSixtyFourPixels()
    {
        Assert.IsNotNull(Classify(new PixelRect(500, 1000, 64, 80)));
        Assert.IsNull(Classify(new PixelRect(500, 1000, 63, 80)));
    }

    [TestMethod]
    public void EdgeFlushToleranceIsTwoPixels()
    {
        Assert.AreEqual(ScreenEdge.Bottom, Classify(new PixelRect(96, 997, 1728, 81))?.Edge, "2 px short of bottom");
        Assert.IsNull(Classify(new PixelRect(96, 996, 1728, 81)), "3 px short of bottom");
        Assert.AreEqual(ScreenEdge.Top, Classify(new PixelRect(0, 2, 1920, 40))?.Edge, "2 px below top");
        Assert.IsNull(Classify(new PixelRect(0, 3, 1920, 40)), "3 px below top");
    }

    [TestMethod]
    public void CornerChunkTakesTheThinnerEdge()
    {
        // Flush with both left and bottom: 70 wide (left) beats 81 tall (bottom).
        Assert.AreEqual(ScreenEdge.Left, Classify(new PixelRect(0, 999, 70, 81))?.Edge);
    }

    [TestMethod]
    public void FullWidthTopBarIsNotMistakenForSideChrome()
    {
        // Touches left and right too, but as side chrome it would be 1920 thick.
        Assert.AreEqual(ScreenEdge.Top, Classify(Desk.TopBar)?.Edge);
    }

    [TestMethod]
    public void RectsOffEveryMonitorAreIgnored()
    {
        Assert.IsNull(Classify(new PixelRect(5000, 0, 400, 40)));
        Assert.IsNull(Classify(new PixelRect(500, 1000, 0, 80)));
        Assert.IsNull(DockAvoidance.Classify(Desk.TopBar, Array.Empty<PixelRect>()));
    }

    [TestMethod]
    public void ClassifyAllKeepsOnlyChromeFromTheRealRegion()
    {
        var region = new[] { Desk.TopBar, Desk.Dock, new PixelRect(710, 40, 500, 400) };
        var zones = DockAvoidance.ClassifyAll(region, Desk.Monitors);
        CollectionAssert.AreEquivalent(new[] { ScreenEdge.Top, ScreenEdge.Bottom }, zones.Select(z => z.Edge).ToArray());
    }
}

[TestClass]
public class CoalesceBandsTests
{
    [TestMethod]
    public void RejoinsPanelSplitIntoBands()
    {
        // How the X server stores "left panel + bottom dock": the panel is cut
        // at y=999, where the dock's band starts.
        var banded = new[]
        {
            new PixelRect(0, 40, 70, 959),
            new PixelRect(0, 999, 70, 81),
            new PixelRect(96, 999, 1728, 81),
        };
        var merged = DockAvoidance.CoalesceBands(banded);
        CollectionAssert.AreEquivalent(new[] { Desk.LeftDock, Desk.Dock }, merged.ToArray());

        var zones = DockAvoidance.ClassifyAll(banded, Desk.Monitors);
        CollectionAssert.AreEquivalent(new[] { ScreenEdge.Left, ScreenEdge.Bottom }, zones.Select(z => z.Edge).ToArray());
    }

    [TestMethod]
    public void JoinsChainsOfThreeOrMoreBands()
    {
        var banded = new[] { new PixelRect(0, 0, 50, 10), new PixelRect(0, 20, 50, 10), new PixelRect(0, 10, 50, 10) };
        CollectionAssert.AreEqual(new[] { new PixelRect(0, 0, 50, 30) }, DockAvoidance.CoalesceBands(banded).ToArray());
    }

    [TestMethod]
    public void LeavesGapsAndDifferentSpansAlone()
    {
        var gap = new[] { new PixelRect(0, 0, 50, 10), new PixelRect(0, 11, 50, 10) };
        Assert.AreEqual(2, DockAvoidance.CoalesceBands(gap).Count);

        var differentWidth = new[] { new PixelRect(0, 0, 50, 10), new PixelRect(0, 10, 51, 10) };
        Assert.AreEqual(2, DockAvoidance.CoalesceBands(differentWidth).Count);
    }

    [TestMethod]
    public void DropsEmptyRects()
        => Assert.AreEqual(0, DockAvoidance.CoalesceBands(new[] { new PixelRect(0, 0, 0, 10) }).Count);
}

[TestClass]
public class ComputeNudgeTests
{
    private static readonly PixelRect[] RealZones = { Desk.TopBar, Desk.Dock };

    private static void AssertClear(PixelRect window, IEnumerable<PixelRect> zones, PixelRect monitor)
    {
        foreach (var zone in zones)
            Assert.IsFalse(DockAvoidance.Overlaps(window, zone), $"{window} still overlaps {zone}");
        Assert.IsTrue(monitor.Contains(window), $"{window} left its monitor {monitor}");
    }

    [TestMethod]
    public void RealFixtureTimerOverDockSideZoneMovesAboveDockAndStaysOnPrimary()
    {
        // The reported bug: timer dropped in the dock container's invisible
        // left wing (x=96..~350), where the shell eats every click.
        var window = new PixelRect(150, 990, 234, 108);
        var nudge = DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors);

        Assert.IsNotNull(nudge);
        var moved = new PixelRect(nudge.Value, window.Size);
        Assert.AreEqual(150, moved.X, "pushed straight up, no sideways drift");
        Assert.IsTrue(moved.Bottom < 999, $"bottom {moved.Bottom} must be above the dock at y=999");
        Assert.AreEqual(999 - 8, moved.Bottom, "minimal move: exactly the margin above the dock");
        Assert.IsTrue(Desk.Edp.Contains(moved), "stays on eDP-1");
    }

    [TestMethod]
    public void NoNudgeWhenClear()
    {
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(800, 500, 234, 108), RealZones, Desk.Monitors));
    }

    [TestMethod]
    public void TouchingIsNotOverlapping()
    {
        // Bottom edge exactly on the dock's top edge: no shared pixel.
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(500, 999 - 108, 234, 108), RealZones, Desk.Monitors));
        // Top edge exactly on the top bar's bottom.
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(500, 40, 234, 108), RealZones, Desk.Monitors));
    }

    [TestMethod]
    public void TopBarPushesDown()
    {
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(500, 10, 234, 108), RealZones, Desk.Monitors);
        Assert.AreEqual(new PixelPoint(500, 48), nudge);
    }

    [TestMethod]
    public void LeftDockPushesRight()
    {
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(30, 500, 234, 108), new[] { Desk.LeftDock }, Desk.Monitors);
        Assert.AreEqual(new PixelPoint(78, 500), nudge);
    }

    [TestMethod]
    public void RightDockPushesLeft()
    {
        var right = new PixelRect(3840 - 64, 100, 64, 800);
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(3700, 300, 234, 108), new[] { right }, Desk.Monitors);
        Assert.AreEqual(new PixelPoint(3840 - 64 - 8 - 234, 300), nudge);
    }

    [TestMethod]
    public void CornerWhereTopBarMeetsLeftDockResolvesBoth()
    {
        var zones = new[] { Desk.TopBar, Desk.LeftDock };
        var window = new PixelRect(10, 10, 234, 108);
        var nudge = DockAvoidance.ComputeNudge(window, zones, Desk.Monitors);

        Assert.AreEqual(new PixelPoint(78, 48), nudge);
        AssertClear(new PixelRect(nudge!.Value, window.Size), zones, Desk.Edp);
    }

    [TestMethod]
    public void BottomLeftCornerOfLeftDockAndBottomDockResolvesBoth()
    {
        var shortLeftDock = new PixelRect(0, 40, 70, 900);
        var zones = new[] { shortLeftDock, Desk.Dock };
        var window = new PixelRect(20, 920, 234, 108);
        var nudge = DockAvoidance.ComputeNudge(window, zones, Desk.Monitors);

        Assert.AreEqual(new PixelPoint(78, 883), nudge);
        AssertClear(new PixelRect(nudge!.Value, window.Size), zones, Desk.Edp);
    }

    [TestMethod]
    public void ShortLeftDockTipNeedsOnlyOnePush()
    {
        // Overlaps only the bottom tip of a short left dock: one push right, no vertical move.
        var shortLeftDock = new PixelRect(0, 40, 70, 560);
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(10, 560, 234, 108), new[] { Desk.TopBar, shortLeftDock }, Desk.Monitors);
        Assert.AreEqual(new PixelPoint(78, 560), nudge);
    }

    [TestMethod]
    public void MarginIsHonoured()
    {
        var window = new PixelRect(150, 990, 234, 108);
        Assert.AreEqual(new PixelPoint(150, 999 - 108), DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors, margin: 0));
        Assert.AreEqual(new PixelPoint(150, 999 - 20 - 108), DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors, margin: 20));
    }

    [TestMethod]
    public void WindowStraddlingMonitorsIsClampedIntoTheOneHoldingMostOfIt()
    {
        // 120 px on eDP-1, 114 px on HDMI-1, and over the dock's right wing.
        var window = new PixelRect(1800, 990, 234, 108);
        var nudge = DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors);

        Assert.AreEqual(new PixelPoint(1920 - 234, 999 - 8 - 108), nudge);
        AssertClear(new PixelRect(nudge!.Value, window.Size), RealZones, Desk.Edp);
    }

    [TestMethod]
    public void ZonesOnOtherMonitorsDontAffectTheWindow()
    {
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(2000, 990, 234, 90), RealZones, Desk.Monitors));
    }

    [TestMethod]
    public void ReturnsNullWhenNoClearSpotExistsOnTheMonitor()
    {
        // 1000 px tall: can't fit between the top bar (40) and the dock (999).
        var tall = new PixelRect(500, 30, 234, 1000);
        Assert.IsNull(DockAvoidance.ComputeNudge(tall, RealZones, Desk.Monitors));
    }

    [TestMethod]
    public void NonChromeZoneTakesTheShortestWayOut()
    {
        var floating = new PixelRect(800, 400, 200, 200);
        // Exits: up 108, down 158, left 158, right 158.
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(850, 450, 100, 50), new[] { floating }, Desk.Monitors);
        Assert.AreEqual(new PixelPoint(850, 342), nudge);
    }

    [TestMethod]
    public void DegenerateInputsReturnNull()
    {
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(150, 990, 0, 108), RealZones, Desk.Monitors));
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(150, 990, 234, 108), RealZones, Array.Empty<PixelRect>()));
        Assert.IsNull(DockAvoidance.ComputeNudge(new PixelRect(150, 990, 234, 108), Array.Empty<PixelRect>(), Desk.Monitors));
    }

    [TestMethod]
    public void WindowDroppedPartlyOffScreenIntoDockLandsFullyOnScreen()
    {
        // Dragged half past the left edge and down into the dock's left wing.
        var window = new PixelRect(-100, 1000, 234, 108);
        var nudge = DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors);

        Assert.AreEqual(new PixelPoint(0, 999 - 8 - 108), nudge);
    }

    [TestMethod]
    public void EveryWindowPositionAlongTheBottomEndsClear()
    {
        // Sweep: whatever x the user drops at, the result is clear and on eDP-1.
        for (var x = -50; x <= 1920; x += 37)
        {
            var window = new PixelRect(x, 1000, 234, 108);
            if (DockAvoidance.FindMonitor(window, Desk.Monitors) != Desk.Edp) continue;
            var nudge = DockAvoidance.ComputeNudge(window, RealZones, Desk.Monitors);
            if (nudge is null)
            {
                Assert.IsFalse(DockAvoidance.Overlaps(window, Desk.Dock), $"x={x}: overlap but no nudge");
                continue;
            }
            AssertClear(new PixelRect(nudge.Value, window.Size), RealZones, Desk.Edp);
        }
    }
}
