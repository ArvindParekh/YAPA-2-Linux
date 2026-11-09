using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

[TestClass]
public class DockZoneTrackerTests
{
    private static readonly PixelRect HdmiDock = new(2016, 999, 1728, 81);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static PixelRect[] Rects(params (int X, int Y, int W, int H)[] rects)
        => rects.Select(r => new PixelRect(r.X, r.Y, r.W, r.H)).ToArray();

    private static PixelRect ZoneOn(DockZoneTracker tracker, PixelRect monitor, ScreenEdge edge, TimeSpan now)
        => tracker.GetZones(now).Single(z => z.Monitor == monitor && z.Edge == edge).Bounds;

    [TestMethod]
    public void LearnsTopBarAndDockFromRealRegion()
    {
        var tracker = new DockZoneTracker();
        Assert.IsTrue(tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(0)));

        var zones = tracker.GetZones(Ms(0));
        Assert.AreEqual(2, zones.Count);
        Assert.AreEqual(Desk.TopBar, ZoneOn(tracker, Desk.Edp, ScreenEdge.Top, Ms(0)));
        Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(0)));
    }

    [TestMethod]
    public void RemembersDockWhileItIsHidden()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(0));
        // Autohide: the dock's rect leaves the region entirely.
        Assert.IsFalse(tracker.Observe(new[] { Desk.TopBar }, Desk.Monitors, Ms(100)));

        Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(5000)));

        // And it still drives avoidance for a window dropped there meanwhile.
        var nudge = DockAvoidance.ComputeNudge(new PixelRect(150, 990, 234, 108),
            tracker.GetZones(Ms(5000)).Select(z => z.Bounds), Desk.Monitors);
        Assert.AreEqual(new PixelPoint(150, 883), nudge);
    }

    [TestMethod]
    public void IgnoresTransientShellUi()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(0));
        // Overview (full monitor), a calendar popup, a notification banner mid-slide (above the screen).
        var changed = tracker.Observe(new[]
        {
            Desk.TopBar, Desk.Dock, Desk.Edp, new PixelRect(710, 40, 500, 400), new PixelRect(699, -56, 522, 56),
        }, Desk.Monitors, Ms(10));

        Assert.IsFalse(changed);
        Assert.AreEqual(2, tracker.GetZones(Ms(10)).Count);
    }

    [TestMethod]
    public void DockThatGrowsUpdatesInsteadOfAccumulating()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));

        var wider = new PixelRect(60, 999, 1800, 81);
        Assert.IsTrue(tracker.Observe(new[] { wider }, Desk.Monitors, Ms(10)));

        Assert.AreEqual(wider, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(10)));
        Assert.AreEqual(1, tracker.GetZones(Ms(10)).Count);
    }

    [TestMethod]
    public void DockThatShrinksAlongItsEdgeUpdatesImmediately()
    {
        // Fewer icons: shorter and re-centred, same thickness. The old, longer
        // footprint must not linger.
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));

        var shorter = new PixelRect(300, 999, 1320, 81);
        Assert.IsTrue(tracker.Observe(new[] { shorter }, Desk.Monitors, Ms(10)));
        Assert.AreEqual(shorter, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(10)));
    }

    [TestMethod]
    public void ThicknessWithinTwoPixelsCountsAsSameSize()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));

        var slightlyThinner = new PixelRect(96, 1001, 1728, 79);
        Assert.IsTrue(tracker.Observe(new[] { slightlyThinner }, Desk.Monitors, Ms(10)));
        Assert.AreEqual(slightlyThinner, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(10)));
    }

    [TestMethod]
    public void RealSlideInAndOutOfSecondaryDockKeepsFullFootprint()
    {
        // Verbatim from Mutter's stage input shape on the reference desktop
        // (60 Hz frames). HDMI-1's dock slides in and then out again. Its top
        // edge also splits the static eDP-1 dock into two bands.
        var frames = new (double T, PixelRect[] Region)[]
        {
            (0, Rects((0, 0, 1920, 40), (96, 999, 1728, 81))),
            (24, Rects((0, 0, 1920, 40), (96, 999, 1728, 62), (96, 1061, 1728, 19), (2016, 1061, 1728, 19))),
            (40, Rects((0, 0, 1920, 40), (96, 999, 1728, 51), (96, 1050, 1728, 30), (2016, 1050, 1728, 30))),
            (57, Rects((0, 0, 1920, 40), (96, 999, 1728, 40), (96, 1039, 1728, 41), (2016, 1039, 1728, 41))),
            (73, Rects((0, 0, 1920, 40), (96, 999, 1728, 32), (96, 1031, 1728, 49), (2016, 1031, 1728, 49))),
            (90, Rects((0, 0, 1920, 40), (96, 999, 1728, 24), (96, 1023, 1728, 57), (2016, 1023, 1728, 57))),
            (106, Rects((0, 0, 1920, 40), (96, 999, 1728, 17), (96, 1016, 1728, 64), (2016, 1016, 1728, 64))),
            (124, Rects((0, 0, 1920, 40), (96, 999, 1728, 11), (96, 1010, 1728, 70), (2016, 1010, 1728, 70))),
            (140, Rects((0, 0, 1920, 40), (96, 999, 1728, 7), (96, 1006, 1728, 74), (2016, 1006, 1728, 74))),
            (158, Rects((0, 0, 1920, 40), (96, 999, 1728, 3), (96, 1002, 1728, 78), (2016, 1002, 1728, 78))),
            (175, Rects((0, 0, 1920, 40), (96, 999, 1728, 1), (96, 1000, 1728, 80), (2016, 1000, 1728, 80))),
            (192, Rects((0, 0, 1920, 40), (96, 999, 1728, 81), (2016, 999, 1728, 81))),
            // ~2.7 s later it slides back out.
            (2926, Rects((0, 0, 1920, 40), (96, 999, 1728, 25), (96, 1024, 1728, 56), (2016, 1024, 1728, 56))),
            (2943, Rects((0, 0, 1920, 40), (96, 999, 1728, 35), (96, 1034, 1728, 46), (2016, 1034, 1728, 46))),
            (2959, Rects((0, 0, 1920, 40), (96, 999, 1728, 45), (96, 1044, 1728, 36), (2016, 1044, 1728, 36))),
            (2976, Rects((0, 0, 1920, 40), (96, 999, 1728, 53), (96, 1052, 1728, 28), (2016, 1052, 1728, 28))),
            (2992, Rects((0, 0, 1920, 40), (96, 999, 1728, 60), (96, 1059, 1728, 21), (2016, 1059, 1728, 21))),
            (3010, Rects((0, 0, 1920, 40), (96, 999, 1728, 67), (96, 1066, 1728, 14), (2016, 1066, 1728, 14))),
            (3026, Rects((0, 0, 1920, 40), (96, 999, 1728, 72), (96, 1071, 1728, 9), (2016, 1071, 1728, 9))),
            (3043, Rects((0, 0, 1920, 40), (96, 999, 1728, 76), (96, 1075, 1728, 5), (2016, 1075, 1728, 5))),
            (3059, Rects((0, 0, 1920, 40), (96, 999, 1728, 79), (96, 1078, 1728, 2), (2016, 1078, 1728, 2))),
            (3076, Rects((0, 0, 1920, 40), (96, 999, 1728, 80), (96, 1079, 1728, 1), (2016, 1079, 1728, 1))),
            (3092, Rects((0, 0, 1920, 40), (96, 999, 1728, 81))),
        };

        var tracker = new DockZoneTracker();
        foreach (var (t, region) in frames)
        {
            tracker.Observe(region, Desk.Monitors, Ms(t));
            // The band split must never shrink the static eDP-1 dock.
            Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(t)), $"eDP dock at t={t}");
        }

        // Long after it hid, the full HDMI-1 footprint is what we avoid, not a
        // sliver from the last animation frame.
        Assert.AreEqual(HdmiDock, ZoneOn(tracker, Desk.Hdmi, ScreenEdge.Bottom, Ms(60_000)));
        Assert.AreEqual(3, tracker.GetZones(Ms(60_000)).Count);
    }

    [TestMethod]
    public void FirstSightingMidSlideIsUpgradedWhenFullyShown()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(Rects((2016, 1061, 1728, 19)), Desk.Monitors, Ms(0));
        Assert.AreEqual(19, tracker.GetZones(Ms(0)).Single().Thickness, "something beats nothing while it slides in");

        tracker.Observe(new[] { HdmiDock }, Desk.Monitors, Ms(170));
        Assert.AreEqual(HdmiDock, tracker.GetZones(Ms(170)).Single().Bounds);
    }

    [TestMethod]
    public void SteadyThinnerReadingWinsAfterSettling()
    {
        // Icon size reduced: the dock really is thinner now.
        var tracker = new DockZoneTracker(settleTime: Ms(1000));
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));

        var thinner = new PixelRect(96, 1015, 1728, 65);
        Assert.IsFalse(tracker.Observe(new[] { thinner }, Desk.Monitors, Ms(100)), "not yet");
        Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(1099)));
        Assert.AreEqual(thinner, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(1100)));
    }

    [TestMethod]
    public void SettlingIsReportedByObserve()
    {
        var tracker = new DockZoneTracker(settleTime: Ms(1000));
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));
        var thinner = new PixelRect(96, 1015, 1728, 65);
        tracker.Observe(new[] { thinner }, Desk.Monitors, Ms(100));

        // A later, unchanged snapshot (the shell re-sends identical shapes) settles it.
        Assert.IsTrue(tracker.Observe(new[] { thinner }, Desk.Monitors, Ms(1200)));
    }

    [TestMethod]
    public void ChangingThinnerReadingsRestartTheSettleClock()
    {
        var tracker = new DockZoneTracker(settleTime: Ms(1000));
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));
        tracker.Observe(Rects((96, 1015, 1728, 65)), Desk.Monitors, Ms(0));
        var second = new PixelRect(96, 1020, 1728, 60);
        tracker.Observe(new[] { second }, Desk.Monitors, Ms(800));

        Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(1200)), "second reading only 400 ms old");
        Assert.AreEqual(second, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(1800)));
    }

    [TestMethod]
    public void ThinnerReadingThatVanishesIsDiscarded()
    {
        var tracker = new DockZoneTracker(settleTime: Ms(1000));
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));
        tracker.Observe(Rects((96, 1060, 1728, 20)), Desk.Monitors, Ms(10)); // last frame of a slide-out
        tracker.Observe(Array.Empty<PixelRect>(), Desk.Monitors, Ms(26));    // hidden

        Assert.AreEqual(Desk.Dock, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(10_000)));
    }

    [TestMethod]
    public void ThickerReadingReplacesImmediatelyAndCancelsPending()
    {
        var tracker = new DockZoneTracker(settleTime: Ms(1000));
        tracker.Observe(new[] { Desk.Dock }, Desk.Monitors, Ms(0));
        tracker.Observe(Rects((96, 1015, 1728, 65)), Desk.Monitors, Ms(10));
        var thicker = new PixelRect(96, 980, 1728, 100);
        tracker.Observe(new[] { thicker }, Desk.Monitors, Ms(20));

        Assert.AreEqual(thicker, ZoneOn(tracker, Desk.Edp, ScreenEdge.Bottom, Ms(5000)));
    }

    [TestMethod]
    public void EdgesAndMonitorsAreTrackedSeparately()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.TopBar, Desk.Dock, HdmiDock, Desk.LeftDock }, Desk.Monitors, Ms(0));

        var keys = tracker.GetZones(Ms(0)).Select(z => (z.Monitor, z.Edge)).ToHashSet();
        Assert.AreEqual(4, keys.Count);
        Assert.IsTrue(keys.Contains((Desk.Hdmi, ScreenEdge.Bottom)));
        Assert.IsTrue(keys.Contains((Desk.Edp, ScreenEdge.Left)));
    }

    [TestMethod]
    public void SeveralRectsOnOneEdgeInOneSnapshotAreUnited()
    {
        var tracker = new DockZoneTracker();
        // A panel split in two by something between them, same snapshot.
        tracker.Observe(Rects((0, 0, 800, 40), (1000, 0, 920, 40)), Desk.Monitors, Ms(0));

        Assert.AreEqual(Desk.TopBar, tracker.GetZones(Ms(0)).Single().Bounds);
    }

    [TestMethod]
    public void UnchangedSnapshotReportsNoChange()
    {
        var tracker = new DockZoneTracker();
        Assert.IsTrue(tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(0)));
        Assert.IsFalse(tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(16)));
    }

    [TestMethod]
    public void ZonesOnUnpluggedMonitorsAreForgotten()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.TopBar, Desk.Dock, HdmiDock }, Desk.Monitors, Ms(0));

        Assert.IsTrue(tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, new[] { Desk.Edp }, Ms(10)));
        Assert.IsFalse(tracker.GetZones(Ms(10)).Any(z => z.Monitor == Desk.Hdmi));
        Assert.AreEqual(2, tracker.GetZones(Ms(10)).Count);
    }

    [TestMethod]
    public void ClearForgetsEverything()
    {
        var tracker = new DockZoneTracker();
        tracker.Observe(new[] { Desk.TopBar, Desk.Dock }, Desk.Monitors, Ms(0));
        tracker.Clear();
        Assert.AreEqual(0, tracker.GetZones(Ms(0)).Count);
    }
}
