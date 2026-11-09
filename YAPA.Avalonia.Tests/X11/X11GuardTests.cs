using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

/// <summary>
/// The X11 layer must be a silent no-op wherever there is no usable X11
/// desktop. These run everywhere, including headless CI. (MSTest runs a
/// class's tests sequentially, so the environment swaps don't leak.)
/// </summary>
[TestClass]
public class X11GuardTests
{
    private static readonly string[] Vars = { "DISPLAY", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE" };
    private Dictionary<string, string?> _saved = new();

    [TestInitialize]
    public void Save()
    {
        _saved = new Dictionary<string, string?>();
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);
    }

    [TestCleanup]
    public void Restore()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    [TestMethod]
    public void NoDisplayMeansNullMonitorAndFailingHelpers()
    {
        Environment.SetEnvironmentVariable("DISPLAY", null);
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);

        using var monitor = ShellInputRegionMonitor.Create();
        Assert.IsInstanceOfType(monitor, typeof(NullShellInputRegionMonitor));
        monitor.Start();
        Assert.AreEqual(0, monitor.Current.Count);

        Assert.IsFalse(X11Window.SetAbove(new IntPtr(0x1234), true));
        Assert.IsFalse(X11Window.SetSkipTaskbar(new IntPtr(0x1234), true));
        Assert.IsFalse(X11Window.SetOnAllWorkspaces(new IntPtr(0x1234), true));
        Assert.IsFalse(X11Window.SuppressCompositorShadow(new IntPtr(0x1234)));
    }

    [DataTestMethod]
    [DataRow("WAYLAND_DISPLAY", "wayland-0")]
    [DataRow("XDG_SESSION_TYPE", "wayland")]
    [DataRow("XDG_SESSION_TYPE", "Wayland")]
    public void WaylandSessionGetsNullMonitorEvenWithXwaylandDisplay(string variable, string value)
    {
        Environment.SetEnvironmentVariable("DISPLAY", ":0");
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "x11");
        Environment.SetEnvironmentVariable(variable, value);

        using var monitor = ShellInputRegionMonitor.Create();
        Assert.IsInstanceOfType(monitor, typeof(NullShellInputRegionMonitor));
    }

    [TestMethod]
    public void ZeroXidIsRejectedWithoutTouchingX()
    {
        Assert.IsFalse(X11Window.SetAbove(IntPtr.Zero, true));
        Assert.IsFalse(X11Window.SuppressCompositorShadow(IntPtr.Zero));
    }

    [TestMethod]
    public void MonitorOnUnreachableDisplayIdlesAndDisposesPromptly()
    {
        // Nothing listens on display :97 (or, on a CI box with Xvfb there,
        // there is no compositor, and the monitor idles all the same).
        Environment.SetEnvironmentVariable("DISPLAY", ":97");
        var monitor = new X11ShellInputRegionMonitor();
        var raised = 0;
        monitor.Changed += () => raised++;
        monitor.Start();

        var sw = Stopwatch.StartNew();
        monitor.Dispose();
        Assert.IsTrue(sw.ElapsedMilliseconds < 2500, $"dispose took {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(0, monitor.Current.Count);
        Assert.AreEqual(0, raised, "nothing to report, so no change events");
    }
}
