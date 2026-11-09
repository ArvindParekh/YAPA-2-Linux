using System;
using System.Collections.Generic;
using Avalonia;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Keeps a window out of screen areas where it would stop receiving clicks
/// (on GNOME/X11: the shell's dock and top bar input regions).
/// </summary>
public interface IWindowPlacementGuard
{
    /// <summary>Returns where the window should move, or null if it's fine where it is.</summary>
    PixelPoint? Resolve(PixelRect window, IReadOnlyList<PixelRect> monitors);

    /// <summary>
    /// Records the blocked areas as they are right now, before the window is
    /// shown. A window appearing over an autohiding dock makes the dock slide
    /// away, and its first, partly hidden frame is otherwise all the guard sees.
    /// </summary>
    void Prime(IReadOnlyList<PixelRect> monitors);

    /// <summary>The blocked areas changed (e.g. the dock appeared). Raised on any thread.</summary>
    event Action? ZonesChanged;
}

public sealed class NullPlacementGuard : IWindowPlacementGuard
{
    public PixelPoint? Resolve(PixelRect window, IReadOnlyList<PixelRect> monitors) => null;
    public void Prime(IReadOnlyList<PixelRect> monitors) { }

#pragma warning disable CS0067 // never raised: nothing is ever blocked
    public event Action? ZonesChanged;
#pragma warning restore CS0067
}
