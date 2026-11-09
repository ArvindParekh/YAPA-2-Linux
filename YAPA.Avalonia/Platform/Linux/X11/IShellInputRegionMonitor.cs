using System;
using System.Collections.Generic;
using Avalonia;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Watches the screen areas where the desktop shell takes pointer input for
/// itself (on GNOME/X11: the input shape of Mutter's stage window). A client
/// window under those areas can't be clicked.
/// </summary>
public interface IShellInputRegionMonitor : IDisposable
{
    /// <summary>
    /// The current region as rects in root (virtual screen) pixel coordinates.
    /// Empty when unknown or unsupported. The list is immutable and replaced
    /// wholesale, so it is safe to read from any thread.
    /// </summary>
    IReadOnlyList<PixelRect> Current { get; }

    /// <summary>
    /// Raised when <see cref="Current"/> changes. Raised on the monitor's
    /// background thread, so marshal to the UI thread before touching UI.
    /// </summary>
    event Action? Changed;

    /// <summary>Starts watching. Idempotent. Subscribe to <see cref="Changed"/> first.</summary>
    void Start();
}

/// <summary>Used where there is nothing to watch: Wayland, no X server, other OSes.</summary>
public sealed class NullShellInputRegionMonitor : IShellInputRegionMonitor
{
    public IReadOnlyList<PixelRect> Current => Array.Empty<PixelRect>();

    public event Action? Changed { add { } remove { } }

    public void Start() { }

    public void Dispose() { }
}

public static class ShellInputRegionMonitor
{
    /// <summary>
    /// The X11 monitor on an X11 desktop session, otherwise the null one.
    /// Under Wayland the compositor routes shell clicks itself, so nothing
    /// needs avoiding.
    /// </summary>
    public static IShellInputRegionMonitor Create()
        => X11Session.IsX11Desktop ? new X11ShellInputRegionMonitor() : new NullShellInputRegionMonitor();
}
