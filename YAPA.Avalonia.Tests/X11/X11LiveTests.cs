using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

/// <summary>
/// Runs against the live X server, only with YAPA_X11_TESTS=1. The monitor
/// test only reads. The window tests touch nothing but their own throwaway
/// windows.
/// </summary>
[TestClass]
public class X11LiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MonitorReportsShellInputRegionAndStopsCleanly()
    {
        LiveX11.Require();
        var monitors = LiveX11.Monitors();
        foreach (var m in monitors) TestContext.WriteLine($"monitor: {m}");

        var monitor = new X11ShellInputRegionMonitor();
        var snapshots = new ConcurrentQueue<(long Ms, string Rects)>();
        var clock = Stopwatch.StartNew();
        using var first = new ManualResetEventSlim();
        monitor.Changed += () =>
        {
            snapshots.Enqueue((clock.ElapsedMilliseconds, string.Join(" ", monitor.Current)));
            first.Set();
        };
        monitor.Start();

        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(5)), "monitor never reported a region");
        var region = monitor.Current;
        foreach (var r in region) TestContext.WriteLine($"region rect: {r}");
        var zones = DockAvoidance.ClassifyAll(region, monitors);
        foreach (var z in zones) TestContext.WriteLine($"chrome: {z.Edge} {z.Bounds} thickness={z.Thickness} on {z.Monitor}");

        if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            Assert.IsTrue(zones.Any(z => z.Edge == ScreenEdge.Top), "GNOME's top bar should be in the stage input region");

        // Let real ShapeNotify traffic (dock slides, notifications) show up in the log.
        var observeSeconds = int.TryParse(Environment.GetEnvironmentVariable("YAPA_X11_OBSERVE_SECONDS"), out var s) ? s : 2;
        Thread.Sleep(TimeSpan.FromSeconds(observeSeconds));

        var sw = Stopwatch.StartNew();
        monitor.Dispose();
        TestContext.WriteLine($"dispose took {sw.ElapsedMilliseconds} ms");
        Assert.IsTrue(sw.ElapsedMilliseconds < 1000, "eventfd wake-up should stop the thread promptly");
        Assert.AreEqual(0, monitor.Current.Count, "a stopped monitor reports nothing");

        foreach (var (ms, rects) in snapshots) TestContext.WriteLine($"change @{ms}ms: {rects}");
        monitor.Dispose(); // idempotent
    }

    [TestMethod]
    public void MonitorDisposedBeforeStartOrTwiceIsHarmless()
    {
        LiveX11.Require();
        new X11ShellInputRegionMonitor().Dispose();

        var monitor = new X11ShellInputRegionMonitor();
        monitor.Start();
        monitor.Start();
        monitor.Dispose();
        monitor.Dispose();
        monitor.Start(); // after dispose: ignored
        Assert.AreEqual(0, monitor.Current.Count);
    }

    [TestMethod]
    public void StateHelpersRoundTripThroughWindowManagerOnMappedWindow()
    {
        LiveX11.Require();
        using var window = new TestWindow();
        window.Map();
        Assert.IsTrue(LiveX11.WaitFor(() => window.IsManaged), "Mutter never managed the test window");
        TestContext.WriteLine($"test window 0x{window.Xid:x}, initial state: {Describe(window)}");

        Assert.IsTrue(X11Window.SetAbove(window.Xid, true));
        Assert.IsTrue(LiveX11.WaitFor(() => window.HasState("_NET_WM_STATE_ABOVE")), "ABOVE not applied");
        TestContext.WriteLine($"after SetAbove(true): {Describe(window)}");
        Assert.IsTrue(X11Window.SetAbove(window.Xid, false));
        Assert.IsTrue(LiveX11.WaitFor(() => !window.HasState("_NET_WM_STATE_ABOVE")), "ABOVE not removed");

        // TestWindow starts skip-taskbar (see its constructor), so toggle off, then on.
        Assert.IsTrue(window.HasState("_NET_WM_STATE_SKIP_TASKBAR"), "initial skip-taskbar honoured");
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, false));
        Assert.IsTrue(LiveX11.WaitFor(() => !window.HasState("_NET_WM_STATE_SKIP_TASKBAR") && !window.HasState("_NET_WM_STATE_SKIP_PAGER")),
            "SKIP_TASKBAR/SKIP_PAGER not removed");
        TestContext.WriteLine($"after SetSkipTaskbar(false): {Describe(window)}");
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, true));
        Assert.IsTrue(LiveX11.WaitFor(() => window.HasState("_NET_WM_STATE_SKIP_TASKBAR") && window.HasState("_NET_WM_STATE_SKIP_PAGER")),
            "SKIP_TASKBAR/SKIP_PAGER not applied");
        TestContext.WriteLine($"after SetSkipTaskbar(true): {Describe(window)}");

        Assert.IsTrue(X11Window.SetAbove(window.Xid, true));
        Assert.IsTrue(X11Window.SetOnAllWorkspaces(window.Xid, true));
        Assert.IsTrue(LiveX11.WaitFor(() => Desktop(window) == 0xFFFFFFFF), "not on all workspaces");
        TestContext.WriteLine($"after SetAbove(true) + SetOnAllWorkspaces(true): {Describe(window)}");

        // Opt-in pause so the state can be cross-checked with xprop from outside.
        if (int.TryParse(Environment.GetEnvironmentVariable("YAPA_X11_HOLD_MS"), out var holdMs))
            Thread.Sleep(holdMs);
        Assert.IsTrue(X11Window.SetAbove(window.Xid, false));
        Assert.IsTrue(X11Window.SetOnAllWorkspaces(window.Xid, false));
        Assert.IsTrue(LiveX11.WaitFor(() => Desktop(window) is >= 0 and not 0xFFFFFFFF), "still on all workspaces");
        TestContext.WriteLine($"after SetOnAllWorkspaces(false): {Describe(window)}");

        Assert.IsFalse(window.HasState("_NET_WM_STATE_FOCUSED"), "test window must never take the user's focus");
    }

    [TestMethod]
    public void HelpersWritePropertiesOnWithdrawnWindowAndMutterHonoursThemOnMap()
    {
        LiveX11.Require();
        using var window = new TestWindow();

        Assert.IsTrue(X11Window.SuppressCompositorShadow(window.Xid));
        Assert.IsTrue(X11Window.SetAbove(window.Xid, true));
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, true));
        Assert.IsTrue(X11Window.SetOnAllWorkspaces(window.Xid, true));

        CollectionAssert.AreEqual(new IntPtr[] { 0, 0, 0, 0 }, window.Read("_GTK_FRAME_EXTENTS").ToArray());
        Assert.IsTrue(window.HasState("_NET_WM_STATE_ABOVE"));
        Assert.IsTrue(window.HasState("_NET_WM_STATE_SKIP_TASKBAR"));
        Assert.IsTrue(window.HasState("_NET_WM_STATE_SKIP_PAGER"));
        Assert.AreEqual(0xFFFFFFFF, Desktop(window));
        TestContext.WriteLine($"withdrawn, before map: {Describe(window)}");

        // Removal on a withdrawn window edits the property and keeps the rest.
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, false));
        Assert.IsFalse(window.HasState("_NET_WM_STATE_SKIP_TASKBAR"));
        Assert.IsTrue(window.HasState("_NET_WM_STATE_ABOVE"), "unrelated state kept");
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, true));

        window.Map();
        Assert.IsTrue(LiveX11.WaitFor(() => window.IsManaged));
        Assert.IsTrue(LiveX11.WaitFor(() => window.HasState("_NET_WM_STATE_ABOVE")), "Mutter dropped the initial ABOVE state");
        Assert.IsTrue(window.HasState("_NET_WM_STATE_SKIP_TASKBAR"), "Mutter dropped the initial SKIP_TASKBAR state");
        Assert.AreEqual(0xFFFFFFFF, Desktop(window), "Mutter dropped the initial all-workspaces desktop");
        TestContext.WriteLine($"mapped: {Describe(window)}");

        // The Avalonia #16115 mechanism: on withdrawal (Hide) Mutter deletes
        // _NET_WM_STATE and _NET_WM_DESKTOP, so every state is gone on the next Show.
        window.Withdraw();
        Assert.IsTrue(LiveX11.WaitFor(() => !window.IsManaged), "window not withdrawn");
        TestContext.WriteLine($"after withdraw: {Describe(window)}");
        Assert.AreEqual(0, window.Read("_NET_WM_STATE").Count, "Mutter clears state on withdraw");
        Assert.AreEqual(0, window.Read("_NET_WM_DESKTOP").Count, "Mutter clears desktop on withdraw");

        // Re-arm while hidden, then show: comes back with no post-map toggle.
        Assert.IsTrue(X11Window.SetAbove(window.Xid, true));
        Assert.IsTrue(X11Window.SetSkipTaskbar(window.Xid, true));
        window.Map();
        Assert.IsTrue(LiveX11.WaitFor(() => window.IsManaged));
        Assert.IsTrue(LiveX11.WaitFor(() => window.HasState("_NET_WM_STATE_ABOVE")), "ABOVE lost across hide/show");
        TestContext.WriteLine($"re-mapped: {Describe(window)}");
    }

    [TestMethod]
    public void StaleXidFailsWithoutKillingTheProcess()
    {
        LiveX11.Require();
        IntPtr stale;
        using (var window = new TestWindow()) stale = window.Xid;

        // Without the error trap, Xlib's default handler would exit() here on BadWindow.
        Assert.IsFalse(X11Window.SetAbove(stale, true));
        Assert.IsFalse(X11Window.SuppressCompositorShadow(stale));
        Assert.IsFalse(X11Window.SetAbove(IntPtr.Zero, true));
    }

    private static long Desktop(TestWindow window)
        => window.Read("_NET_WM_DESKTOP") is { Count: > 0 } d ? (long)d[0] & 0xFFFFFFFF : -1;

    private static string Describe(TestWindow window)
    {
        var display = X11Session.TryOpen(_ => { });
        try
        {
            var states = window.Read("_NET_WM_STATE").Select(a => AtomName(display, a));
            return $"_NET_WM_STATE=[{string.Join(", ", states)}] _NET_WM_DESKTOP={Desktop(window):X} WM_STATE={(window.IsManaged ? "managed" : "withdrawn")}";
        }
        finally
        {
            X11Session.Close(display);
        }
    }

    private static string AtomName(IntPtr display, IntPtr atom)
    {
        var ptr = XGetAtomName(display, atom);
        if (ptr == IntPtr.Zero) return atom.ToString();
        var name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? "";
        Xlib.XFree(ptr);
        return name;
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);
}
