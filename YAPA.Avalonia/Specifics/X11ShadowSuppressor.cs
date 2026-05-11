using System;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Stamps _NET_WM_WINDOW_TYPE = _NET_WM_WINDOW_TYPE_DOCK on the X11 window
/// after it is mapped to the screen. DOCK-type windows on Mutter (GNOME):
///   - Skip shadow rendering (no transparent halo)
///   - Are excluded from Show Desktop (Super+D leaves them visible)
///   - Are not auto-minimized when the user clicks another window
///
/// UTILITY was previously used here, but Mutter treats parent-less UTILITY
/// windows oddly (auto-minimize on focus loss, hidden during Show Desktop),
/// which fights the desktop-widget behaviour we want.
///
/// Must be called from the window's Opened handler (after the XID is live).
/// No-op on non-Linux platforms or when DISPLAY is unavailable.
/// </summary>
internal static class X11ShadowSuppressor
{
    private const int PropModeReplace = 0;
    private const int XaAtom = 4; // predefined X11 atom for the ATOM type

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(string? display);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XInternAtom")]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName,
        [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    // data is IntPtr[] because on 64-bit Linux, Atom/long is pointer-sized even
    // when the X11 protocol format is 32; XLib expects native-long-sized elements.
    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window,
        IntPtr property, IntPtr type, int format, int mode,
        IntPtr[] data, int nElements);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(IntPtr display);

    /// <param name="xid">X11 window XID from <c>TryGetPlatformHandle().Handle</c>.</param>
    public static void Apply(IntPtr xid)
    {
        if (!OperatingSystem.IsLinux() || xid == IntPtr.Zero) return;

        var display = XOpenDisplay(Environment.GetEnvironmentVariable("DISPLAY"));
        if (display == IntPtr.Zero) return;

        try
        {
            var propAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            var dockAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE_DOCK", false);

            XChangeProperty(display, xid, propAtom, (IntPtr)XaAtom,
                32, PropModeReplace, new[] { dockAtom }, 1);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }
}
