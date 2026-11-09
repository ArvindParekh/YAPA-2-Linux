using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Window-manager hints for one of our own X11 windows, identified by the XID
/// from Avalonia's <c>TryGetPlatformHandle()</c> (HandleDescriptor "XID").
///
/// Each call opens a short-lived private connection. These are rare, UI-driven
/// calls, and a private connection keeps Avalonia's display and its event
/// queue untouched. All methods return false, without throwing, when there is
/// no X server, libX11 is missing, or the server reported an error. True only
/// means the request was delivered; the window manager may still decline it.
///
/// Mapped vs. unmapped follows EWMH: a managed window's state belongs to
/// the window manager, so changes go to the root window as client messages
/// (property writes would be ignored or overwritten). A withdrawn window
/// (never shown, or hidden) has no window manager state yet, so the property
/// is written directly and the window manager reads it at the next map.
/// </summary>
public static class X11Window
{
    // _NET_WM_STATE client-message actions.
    private const int StateRemove = 0;
    private const int StateAdd = 1;

    // EWMH source indication: 1 = normal application. Pagers send 2.
    private const int SourceApplication = 1;

    // The IntPtr(long) constructor rather than a cast: a constant cast trips
    // the "may overflow nint" check even though Xlib's long is 64-bit here.
    private static readonly IntPtr AllDesktops = new(0xFFFFFFFFL);

    /// <summary>
    /// Stops Mutter from drawing its compositor shadow around the window.
    /// Must be called before the window is first shown.
    ///
    /// The mechanism, measured on GNOME 46 / Mutter 46 / X11: Mutter's
    /// has_shadow() returns false for a window that sets _GTK_FRAME_EXTENTS,
    /// on the assumption that it draws its own shadow. Setting all-zero
    /// extents claims that without changing any geometry, and the window keeps
    /// its NORMAL type (focus, minimize, taskbar, drag and right-click all
    /// keep working, unlike the UTILITY and DOCK types tried earlier).
    ///
    /// Mutter reads the property only when it starts managing the window.
    /// Setting it on a mapped window has no effect until the window is
    /// unmapped and mapped again.
    ///
    /// Mutter also never shadows a window it sees as non-opaque: an ARGB
    /// (32-bit visual) window without a full _NET_WM_OPAQUE_REGION. The
    /// transparent timer normally falls into that case already. This call
    /// covers it when the window ends up with an opaque visual, e.g. when
    /// no compositor was running when it was created.
    /// </summary>
    public static bool SuppressCompositorShadow(IntPtr xid) => Run(xid, nameof(SuppressCompositorShadow), display =>
    {
        // Order: left, right, top, bottom.
        Xlib.XChangeProperty(display, xid, Atom(display, "_GTK_FRAME_EXTENTS"), Xlib.XA_CARDINAL, 32,
            Xlib.PropModeReplace, new IntPtr[] { 0, 0, 0, 0 }, 4);
    });

    /// <summary>
    /// Keeps the window above normal windows (_NET_WM_STATE_ABOVE).
    ///
    /// Exists because of Avalonia issue #16115. The window manager drops
    /// _NET_WM_STATE when a window is withdrawn (Hide), and Avalonia doesn't
    /// re-send Topmost on Show because its Topmost value never changed. Calling
    /// this while hidden writes the property, so the next map comes up above.
    /// Calling it while shown asks the window manager directly.
    /// </summary>
    public static bool SetAbove(IntPtr xid, bool above)
        => Run(xid, nameof(SetAbove), display => ChangeState(display, xid, above, "_NET_WM_STATE_ABOVE"));

    /// <summary>
    /// Hides the window from the taskbar/dash and from workspace pagers
    /// (_NET_WM_STATE_SKIP_TASKBAR + _NET_WM_STATE_SKIP_PAGER). Avalonia's
    /// ShowInTaskbar drives SKIP_TASKBAR too, so don't fight it by using both.
    /// </summary>
    public static bool SetSkipTaskbar(IntPtr xid, bool skip)
        => Run(xid, nameof(SetSkipTaskbar), display =>
            ChangeState(display, xid, skip, "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"));

    /// <summary>
    /// Shows the window on every workspace (_NET_WM_DESKTOP = 0xFFFFFFFF;
    /// Mutter adds _NET_WM_STATE_STICKY). Turning it off pins the window to the
    /// current workspace.
    ///
    /// With GNOME's default workspaces-only-on-primary, a window on a
    /// secondary monitor shows on every workspace anyway, even though Mutter
    /// then reports it on the current desktop.
    ///
    /// Like every EWMH state, this is lost when the window is hidden: Mutter
    /// deletes _NET_WM_DESKTOP and _NET_WM_STATE on withdrawal. Re-apply it
    /// before or after each Show.
    /// </summary>
    public static bool SetOnAllWorkspaces(IntPtr xid, bool onAll) => Run(xid, nameof(SetOnAllWorkspaces), display =>
    {
        var netWmDesktop = Atom(display, "_NET_WM_DESKTOP");
        if (IsManaged(display, xid))
        {
            var desktop = onAll ? AllDesktops : CurrentDesktop(display);
            SendToRoot(display, xid, netWmDesktop, desktop, SourceApplication);
        }
        else if (onAll)
        {
            Xlib.XChangeProperty(display, xid, netWmDesktop, Xlib.XA_CARDINAL, 32, Xlib.PropModeReplace,
                new[] { AllDesktops }, 1);
        }
        else
        {
            // Unset means "window manager's choice", which is the current workspace.
            Xlib.XDeleteProperty(display, xid, netWmDesktop);
        }
    });

    private static void ChangeState(IntPtr display, IntPtr xid, bool add, params string[] stateNames)
    {
        var netWmState = Atom(display, "_NET_WM_STATE");
        var states = stateNames.Select(n => Atom(display, n)).ToArray();

        if (IsManaged(display, xid))
        {
            // One message carries up to two properties, which is all we ever need.
            SendToRoot(display, xid, netWmState, add ? StateAdd : StateRemove,
                states[0], states.Length > 1 ? states[1] : IntPtr.Zero, SourceApplication);
            return;
        }

        var current = ReadLongs(display, xid, netWmState);
        var updated = (add ? current.Union(states) : current.Except(states)).ToArray();
        Xlib.XChangeProperty(display, xid, netWmState, Xlib.XA_ATOM, 32, Xlib.PropModeReplace, updated, updated.Length);
    }

    /// <summary>
    /// Whether a window manager currently manages the window. ICCCM: the
    /// window manager puts WM_STATE on a window when it manages it (Normal or
    /// Iconic) and removes it on withdrawal. That differs from map state: a
    /// minimized window is unmapped but still managed.
    /// </summary>
    private static bool IsManaged(IntPtr display, IntPtr xid)
    {
        var wmState = ReadLongs(display, xid, Atom(display, "WM_STATE"));
        const long withdrawn = 0;
        return wmState.Count > 0 && (long)wmState[0] != withdrawn;
    }

    private static IntPtr CurrentDesktop(IntPtr display)
    {
        var value = ReadLongs(display, Xlib.XDefaultRootWindow(display), Atom(display, "_NET_CURRENT_DESKTOP"));
        return value.Count > 0 ? value[0] : IntPtr.Zero;
    }

    private static void SendToRoot(IntPtr display, IntPtr xid, IntPtr messageType,
        IntPtr l0, IntPtr l1, IntPtr l2 = default, IntPtr l3 = default)
    {
        var message = new XClientMessageEvent
        {
            type = Xlib.ClientMessage,
            display = display,
            window = xid,
            message_type = messageType,
            format = 32,
            l0 = l0,
            l1 = l1,
            l2 = l2,
            l3 = l3,
        };
        // EWMH: send to the root with both substructure masks so the window
        // manager, which holds SubstructureRedirect on the root, receives it.
        Xlib.XSendEvent(display, Xlib.XDefaultRootWindow(display), false,
            (IntPtr)(Xlib.SubstructureRedirectMask | Xlib.SubstructureNotifyMask), ref message);
    }

    /// <summary>Reads a format-32 property as C longs (Xlib widens 32-bit items to long).</summary>
    internal static IReadOnlyList<IntPtr> ReadLongs(IntPtr display, IntPtr window, IntPtr property)
    {
        var status = Xlib.XGetWindowProperty(display, window, property, IntPtr.Zero, (IntPtr)1024, false,
            Xlib.AnyPropertyType, out _, out var format, out var count, out _, out var data);
        try
        {
            if (status != Xlib.Success || data == IntPtr.Zero || format != 32) return Array.Empty<IntPtr>();
            var result = new IntPtr[(int)count];
            for (var i = 0; i < result.Length; i++) result[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
            return result;
        }
        finally
        {
            if (data != IntPtr.Zero) Xlib.XFree(data);
        }
    }

    private static IntPtr Atom(IntPtr display, string name) => Xlib.XInternAtom(display, name, false);

    private static bool Run(IntPtr xid, string operation, Action<IntPtr> body)
    {
        if (xid == IntPtr.Zero) return false;

        X11Error? failure = null;
        // The trap runs synchronously on this thread, inside XSync below.
        var display = X11Session.TryOpen(error => failure ??= error);
        if (display == IntPtr.Zero) return false;
        try
        {
            body(display);
            // Round trip so errors (e.g. BadWindow for a stale XID) arrive
            // before we close and can be reported.
            Xlib.XSync(display, false);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] {operation} unavailable: {e.Message}");
            return false;
        }
        finally
        {
            X11Session.Close(display);
        }

        if (failure is { } error)
        {
            Trace.WriteLine($"[x11] {operation} on 0x{xid:x} failed: X error {error.ErrorCode} (request {error.RequestCode})");
            return false;
        }
        return true;
    }
}
