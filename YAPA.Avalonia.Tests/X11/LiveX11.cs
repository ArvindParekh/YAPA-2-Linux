using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

/// <summary>
/// Gate and helpers for tests that talk to the real X server. They only run
/// with YAPA_X11_TESTS=1, so CI and headless runs skip them.
/// </summary>
internal static class LiveX11
{
    public static void Require()
    {
        if (Environment.GetEnvironmentVariable("YAPA_X11_TESTS") != "1")
            Assert.Inconclusive("Live X11 test: set YAPA_X11_TESTS=1 to run against the current X server.");
        if (!X11Session.IsX11Desktop)
            Assert.Inconclusive("Live X11 test: not an X11 desktop session.");
    }

    public static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    /// <summary>Monitor bounds via Xinerama (what Mutter reports to clients).</summary>
    public static IReadOnlyList<PixelRect> Monitors()
    {
        var display = X11Session.TryOpen(_ => { });
        try
        {
            var list = TestXlib.XineramaQueryScreens(display, out var count);
            var result = new List<PixelRect>();
            for (var i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<TestXlib.XineramaScreenInfo>(list + i * Marshal.SizeOf<TestXlib.XineramaScreenInfo>());
                result.Add(new PixelRect(s.x_org, s.y_org, s.width, s.height));
            }
            if (list != IntPtr.Zero) Xlib.XFree(list);
            return result;
        }
        finally
        {
            X11Session.Close(display);
        }
    }
}

/// <summary>
/// A throwaway, undecorated, unfocused window on the right half of HDMI-1,
/// out of the user's way. Only our own windows are ever modified.
/// </summary>
internal sealed class TestWindow : IDisposable
{
    public const int X = 3400, Y = 200;

    private readonly IntPtr _display;

    public IntPtr Xid { get; }

    public TestWindow(int x = X, int y = Y)
    {
        _display = X11Session.TryOpen(e => Trace.WriteLine($"[test] X error {e.ErrorCode} on 0x{e.ResourceId:x}"));
        Assert.AreNotEqual(IntPtr.Zero, _display, "XOpenDisplay");
        var root = Xlib.XDefaultRootWindow(_display);
        Xid = TestXlib.XCreateSimpleWindow(_display, root, x, y, 120, 80, 0, IntPtr.Zero, new IntPtr(0x404040));
        TestXlib.XStoreName(_display, Xid, "yapa-x11-live-test");

        // WM_NORMAL_HINTS wire format: 18 CARD32s, flags first, then x, y.
        // USPosition|PPosition makes Mutter honour our spot instead of
        // auto-placing the window over the user's work.
        var hints = new IntPtr[18];
        hints[0] = 1 | 4;
        hints[1] = x;
        hints[2] = y;
        Set("WM_NORMAL_HINTS", Atom("WM_SIZE_HINTS"), hints);
        Set("_MOTIF_WM_HINTS", Atom("_MOTIF_WM_HINTS"), new IntPtr[] { 2, 0, 0, 0, 0 });
        // Never take the user's keyboard focus. User time 0 alone isn't enough:
        // Mutter still focused the window when it was raised to ABOVE, and
        // flagged DEMANDS_ATTENTION (a shell "is ready" notification) when it
        // refused. WM_HINTS input=False (wire format: flags=InputHint, input=0)
        // takes the window out of focus handling entirely.
        Set("_NET_WM_USER_TIME", Xlib.XA_CARDINAL, new IntPtr[] { 0 });
        Set("WM_HINTS", Atom("WM_HINTS"), new IntPtr[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        // Mutter still marks an unfocused new window that overlaps the focused
        // one as DEMANDS_ATTENTION. Starting out skip-taskbar keeps GNOME Shell
        // from turning that into an "is ready" notification. Tests toggle from here.
        Set("_NET_WM_STATE", Xlib.XA_ATOM, new[] { Atom("_NET_WM_STATE_SKIP_TASKBAR"), Atom("_NET_WM_STATE_SKIP_PAGER") });
        Xlib.XSync(_display, false);
    }

    public void Map() { TestXlib.XMapWindow(_display, Xid); Xlib.XSync(_display, false); }

    /// <summary>ICCCM withdrawal, the same thing Avalonia's Hide() does.</summary>
    public void Withdraw() { TestXlib.XWithdrawWindow(_display, Xid, Xlib.XDefaultScreen(_display)); Xlib.XSync(_display, false); }

    public bool IsManaged => Read("WM_STATE") is { Count: > 0 } s && (long)s[0] != 0;

    public IReadOnlyList<IntPtr> Read(string property)
    {
        Xlib.XSync(_display, false);
        return X11Window.ReadLongs(_display, Xid, Atom(property));
    }

    public bool HasState(string state) => Read("_NET_WM_STATE").Contains(Atom(state));

    public IntPtr Atom(string name) => Xlib.XInternAtom(_display, name, false);

    private void Set(string property, IntPtr type, IntPtr[] data)
        => Xlib.XChangeProperty(_display, Xid, Atom(property), type, 32, Xlib.PropModeReplace, data, data.Length);

    public void Dispose()
    {
        TestXlib.XDestroyWindow(_display, Xid);
        Xlib.XSync(_display, false);
        X11Session.Close(_display);
    }
}

internal static class TestXlib
{
    [DllImport("libX11.so.6")]
    public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y,
        uint width, uint height, uint borderWidth, IntPtr border, IntPtr background);

    [DllImport("libX11.so.6")] public static extern int XMapWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] public static extern int XWithdrawWindow(IntPtr display, IntPtr window, int screen);
    [DllImport("libX11.so.6")] public static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] public static extern int XStoreName(IntPtr display, IntPtr window, string name);

    [StructLayout(LayoutKind.Sequential)]
    public struct XineramaScreenInfo
    {
        public int screen_number;
        public short x_org, y_org, width, height;
    }

    [DllImport("libXinerama.so.1")] public static extern IntPtr XineramaQueryScreens(IntPtr display, out int count);
}
