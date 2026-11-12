using System;
using Avalonia.Controls;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Window-manager state Avalonia doesn't expose, or loses: on X11 the window
/// manager deletes _NET_WM_STATE and _NET_WM_DESKTOP whenever a window is
/// hidden, which is why always-on-top used to vanish after hide/show.
/// Applying the states again while the window is withdrawn makes them stick at
/// the next map; applying them while it is mapped uses EWMH client messages.
/// Every call is a no-op off X11.
/// </summary>
public static class WindowShellIntegration
{
    // One per process; its X connection (and so the anchor window) lives until exit.
    private static readonly Lazy<X11DesktopAnchor?> Anchor = new(X11DesktopAnchor.TryCreate);

    /// <summary>
    /// For a window that appears on its own (a notice): above others, out of the taskbar,
    /// and transient for the desktop anchor, so mapping it doesn't end Show Desktop.
    /// </summary>
    public static void ApplyFloating(Window window, GeneralSettings settings)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
        var xid = handle.Handle;
        X11Window.SuppressCompositorShadow(xid);
        X11Window.SetAbove(xid, true);
        X11Window.SetSkipTaskbar(xid, true);
        X11Window.SetOnAllWorkspaces(xid, settings.ShowOnAllWorkspaces);
        Anchor.Value?.Attach(xid, true);
    }

    public static void Apply(Window window, GeneralSettings settings)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        var xid = handle.Handle;
        if (xid == IntPtr.Zero) return;

        // Mutter never shadows ARGB windows; this only matters if a compositor
        // ever hands us an opaque visual, where it prevents a dark halo.
        X11Window.SuppressCompositorShadow(xid);
        X11Window.SetAbove(xid, settings.AlwaysOnTop);
        X11Window.SetSkipTaskbar(xid, !settings.ShowInTaskbar);
        X11Window.SetOnAllWorkspaces(xid, settings.ShowOnAllWorkspaces);
        Anchor.Value?.Attach(xid, settings.StayOnShowDesktop);
    }
}
