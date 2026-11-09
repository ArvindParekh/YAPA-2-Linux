using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace YAPA.Avalonia.Platform;

public static class WindowPlacement
{
    /// <summary>How much of the window must be on a screen for a saved position to count as usable.</summary>
    private static readonly PixelSize MinimumVisible = new(48, 24);
    private const int DefaultMargin = 24;

    /// <summary>
    /// Picks where to put the timer at startup: the saved position if it is still
    /// meaningfully on screen (monitors get unplugged), otherwise the top-right
    /// corner of the primary work area. Top-right because that corner is rarely
    /// claimed by docks and keeps the timer near the clock.
    /// </summary>
    public static PixelPoint Restore(PixelPoint? saved, PixelSize size, IReadOnlyList<PixelRect> workAreas, PixelRect? primaryWorkArea)
    {
        if (saved is { } p)
        {
            var rect = new PixelRect(p, size);
            if (workAreas.Any(area => IsMeaningfullyVisible(rect, area)))
                return p;
        }

        var target = primaryWorkArea ?? workAreas.FirstOrDefault();
        if (target.Width == 0) return new PixelPoint(DefaultMargin, DefaultMargin);

        return new PixelPoint(
            target.Right - size.Width - DefaultMargin,
            target.Y + DefaultMargin);
    }

    private static bool IsMeaningfullyVisible(PixelRect window, PixelRect area)
    {
        var overlap = window.Intersect(area);
        return overlap.Width >= Math.Min(MinimumVisible.Width, window.Width)
            && overlap.Height >= Math.Min(MinimumVisible.Height, window.Height);
    }
}
