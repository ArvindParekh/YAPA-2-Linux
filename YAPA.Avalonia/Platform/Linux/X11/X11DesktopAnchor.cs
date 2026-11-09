using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Keeps the timer on screen through "Show Desktop" (Super+D) without fighting
/// the window manager.
///
/// Mutter hides every window in show-desktop mode except DOCK/DESKTOP windows
/// and windows transient for one (<c>meta_window_showing_on_its_workspace</c>,
/// via <c>is_desktop_or_dock_foreach</c> over the window and its ancestors).
/// Activating such a window also doesn't end show-desktop mode
/// (<c>maybe_leave_show_desktop_mode</c>). Making the timer itself a DOCK breaks
/// move-drag, focus and right-click, so instead the timer stays NORMAL and
/// declares itself transient for this anchor: an invisible, click-through,
/// off-screen 1x1 DOCK window that lives for as long as the app does.
///
/// Earlier builds reverted the WM's minimize instead, and that looped against
/// Mutter until the session hung.
/// </summary>
public sealed class X11DesktopAnchor : IDisposable
{
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;

    private readonly object _gate = new();
    private IntPtr _display;
    private IntPtr _anchor;

    private X11DesktopAnchor(IntPtr display, IntPtr anchor)
    {
        _display = display;
        _anchor = anchor;
    }

    /// <summary>Creates the anchor, or returns null when not on an X11 desktop.</summary>
    public static X11DesktopAnchor? TryCreate()
    {
        if (!X11Session.IsXServerConfigured) return null;

        var display = X11Session.TryOpen(e => Trace.WriteLine($"[x11] anchor: X error {e.ErrorCode} (request {e.RequestCode})"));
        if (display == IntPtr.Zero) return null;

        try
        {
            var root = Xlib.XDefaultRootWindow(display);
            // Off-screen and 1x1: Mutter doesn't constrain DOCK windows on screen,
            // and even if it did, an empty input shape means it can't catch clicks.
            var anchor = Native.XCreateSimpleWindow(display, root, -64, -64, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
            if (anchor == IntPtr.Zero)
            {
                X11Session.Close(display);
                return null;
            }

            var type = Xlib.XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            var dock = Xlib.XInternAtom(display, "_NET_WM_WINDOW_TYPE_DOCK", false);
            Xlib.XChangeProperty(display, anchor, type, Xlib.XA_ATOM, 32, Xlib.PropModeReplace, [dock], 1);

            var name = Xlib.XInternAtom(display, "_NET_WM_NAME", false);
            var utf8 = Xlib.XInternAtom(display, "UTF8_STRING", false);
            var title = System.Text.Encoding.UTF8.GetBytes("YAPA 2 desktop anchor");
            Native.XChangePropertyBytes(display, anchor, name, utf8, 8, Xlib.PropModeReplace, title, title.Length);

            Native.XShapeCombineRectangles(display, anchor, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
            Native.XMapWindow(display, anchor);
            Xlib.XSync(display, false);

            Trace.WriteLine($"[x11] desktop anchor 0x{anchor:x} created");
            return new X11DesktopAnchor(display, anchor);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] desktop anchor unavailable: {e.Message}");
            X11Session.Close(display);
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="xid"/> survive show-desktop (or not). Safe to call
    /// repeatedly; the WM picks up WM_TRANSIENT_FOR changes on mapped windows.
    /// </summary>
    public void Attach(IntPtr xid, bool attach)
    {
        lock (_gate)
        {
            if (_display == IntPtr.Zero || xid == IntPtr.Zero) return;
            if (attach) Native.XSetTransientForHint(_display, xid, _anchor);
            else Xlib.XDeleteProperty(_display, xid, Xlib.XInternAtom(_display, "WM_TRANSIENT_FOR", false));
            Xlib.XSync(_display, false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_display == IntPtr.Zero) return;
            // Closing the connection destroys the anchor; transients simply lose their parent.
            X11Session.Close(_display);
            _display = IntPtr.Zero;
            _anchor = IntPtr.Zero;
        }
    }

    private static class Native
    {
        [DllImport("libX11.so.6")]
        public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
            uint borderWidth, IntPtr border, IntPtr background);

        [DllImport("libX11.so.6")]
        public static extern int XMapWindow(IntPtr display, IntPtr window);

        [DllImport("libX11.so.6")]
        public static extern int XSetTransientForHint(IntPtr display, IntPtr window, IntPtr parent);

        [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
        public static extern int XChangePropertyBytes(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
            int format, int mode, byte[] data, int elements);

        [DllImport("libXext.so.6")]
        public static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y,
            IntPtr rectangles, int count, int op, int ordering);
    }
}
