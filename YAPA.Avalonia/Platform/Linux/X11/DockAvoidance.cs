using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>The monitor edge a piece of shell chrome is attached to.</summary>
public enum ScreenEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// A rectangle of the shell's input region that looks like persistent chrome
/// (a panel or a dock) rather than a transient popup.
/// </summary>
/// <param name="Bounds">The rect in root (virtual screen) coordinates.</param>
/// <param name="Edge">The monitor edge it is flush with.</param>
/// <param name="Monitor">Bounds of the monitor it belongs to.</param>
public readonly record struct ChromeZone(PixelRect Bounds, ScreenEdge Edge, PixelRect Monitor)
{
    /// <summary>Extent perpendicular to the edge: how far the chrome reaches into the monitor.</summary>
    public int Thickness => Edge is ScreenEdge.Top or ScreenEdge.Bottom ? Bounds.Height : Bounds.Width;

    /// <summary>Extent along the edge.</summary>
    public int Length => Edge is ScreenEdge.Top or ScreenEdge.Bottom ? Bounds.Width : Bounds.Height;
}

/// <summary>
/// Pure geometry for keeping a small window out of the areas where the desktop
/// shell swallows clicks. No X11 here, so every rule is unit-testable.
/// </summary>
public static class DockAvoidance
{
    /// <summary>
    /// How far from a monitor edge a rect may sit and still count as flush. The
    /// shell's region is built from actor allocations, which can be off by a
    /// pixel after fractional scaling or animations.
    /// </summary>
    public const int EdgeTolerance = 2;

    /// <summary>Absolute ceiling on chrome thickness. Real docks and panels sit well below this.</summary>
    public const int MaxThickness = 200;

    /// <summary>
    /// Relative ceiling on chrome thickness. Anything deeper is the overview, a
    /// fullscreen shield or a big popup: transient, and not something to dodge.
    /// </summary>
    public const double MaxThicknessFraction = 0.25;

    /// <summary>
    /// Minimum length along the edge. It filters out slivers such as a
    /// hot-corner pixel or a single notification badge.
    /// </summary>
    public const int MinLength = 64;

    /// <summary>
    /// Classifies one rect. Returns null for anything that isn't persistent chrome.
    /// </summary>
    public static ChromeZone? Classify(PixelRect rect, IReadOnlyList<PixelRect> monitors)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        // Only real overlap counts here: off-screen input is no chrome of ours.
        if (FindOverlappingMonitor(rect, monitors) is not { } monitor) return null;

        ChromeZone? best = null;
        foreach (var edge in Edges)
        {
            if (!IsFlush(rect, monitor, edge)) continue;

            var zone = new ChromeZone(rect, edge, monitor);
            var monitorDepth = edge is ScreenEdge.Top or ScreenEdge.Bottom ? monitor.Height : monitor.Width;
            var maxThickness = Math.Min(MaxThickness, (int)(monitorDepth * MaxThicknessFraction));
            if (zone.Thickness > maxThickness || zone.Length < MinLength) continue;

            // A corner chunk can be flush with two edges (e.g. where a left dock
            // meets a bottom band). The thinner reading is the one that
            // describes how the chrome actually hugs the screen.
            if (best is null || zone.Thickness < best.Value.Thickness) best = zone;
        }
        return best;
    }

    /// <summary>
    /// Coalesces the region's rects and keeps only the chrome. Expects rects in
    /// root coordinates, e.g. straight from <see cref="IShellInputRegionMonitor.Current"/>.
    /// </summary>
    public static IReadOnlyList<ChromeZone> ClassifyAll(IEnumerable<PixelRect> regionRects, IReadOnlyList<PixelRect> monitors)
    {
        var result = new List<ChromeZone>();
        foreach (var rect in CoalesceBands(regionRects))
        {
            if (Classify(rect, monitors) is { } zone) result.Add(zone);
        }
        return result;
    }

    /// <summary>
    /// Re-joins rects that the X server's region code split apart. An X region
    /// is stored as y-x banded rectangles: every distinct top/bottom of any rect
    /// starts a new band, so one tall left panel next to a bottom dock comes
    /// back as two stacked pieces. Joining vertically adjacent pieces with the
    /// same x-span restores the original shape before classification.
    /// </summary>
    public static IReadOnlyList<PixelRect> CoalesceBands(IEnumerable<PixelRect> rects)
    {
        var pending = rects.Where(r => r.Width > 0 && r.Height > 0)
                           .OrderBy(r => r.X).ThenBy(r => r.Width).ThenBy(r => r.Y)
                           .ToList();
        var merged = new List<PixelRect>(pending.Count);
        foreach (var rect in pending)
        {
            // Sorted by (x, width, y), so a stackable piece is always the last one added.
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.X == rect.X && last.Width == rect.Width && last.Bottom == rect.Y)
                {
                    merged[^1] = new PixelRect(last.X, last.Y, last.Width, rect.Bottom - last.Y);
                    continue;
                }
            }
            merged.Add(rect);
        }
        return merged;
    }

    /// <summary>
    /// Computes where to move <paramref name="window"/> so that it overlaps none
    /// of <paramref name="zones"/>, keeping <paramref name="margin"/> pixels of
    /// clearance. Each zone pushes the window perpendicular to the edge it is
    /// attached to (bottom dock: up, left dock: right). A zone that isn't flush
    /// with any edge may push in any direction. The result stays inside the
    /// monitor that holds most of the window.
    /// </summary>
    /// <returns>The new top-left position, or null when the window is already
    /// clear or no position on its monitor is clear.</returns>
    public static PixelPoint? ComputeNudge(PixelRect window, IEnumerable<PixelRect> zones,
                                           IReadOnlyList<PixelRect> monitorBounds, int margin = 8)
    {
        if (window.Width <= 0 || window.Height <= 0) return null;
        if (FindMonitor(window, monitorBounds) is not { } monitor) return null;

        var zoneList = zones.Where(z => z.Width > 0 && z.Height > 0).ToList();
        if (!zoneList.Any(z => Overlaps(window, z))) return null;

        var pushes = zoneList.Select(z => (Zone: z, Directions: PushDirections(z, monitorBounds))).ToList();

        // Small exhaustive search. Each step resolves one overlapping zone by
        // moving perpendicular to it, then clamps into the monitor. Resolving
        // one zone can push the window into another (a corner where the top bar
        // and a left dock meet), so the search chains up to MaxSteps moves and
        // keeps the clear position closest to where the user put the window.
        // Zone counts are tiny (a panel and a dock per monitor), so the
        // branching never gets large.
        const int MaxSteps = 4;
        PixelPoint? best = null;
        var bestCost = long.MaxValue;
        var frontier = new List<PixelRect> { window };
        var seen = new HashSet<PixelPoint> { window.Position };

        for (var step = 0; step < MaxSteps && frontier.Count > 0; step++)
        {
            var next = new List<PixelRect>();
            foreach (var current in frontier)
            {
                foreach (var (zone, directions) in pushes)
                {
                    if (!Overlaps(current, zone)) continue;
                    foreach (var direction in directions)
                    {
                        var moved = Clamp(Push(current, zone, direction, margin), monitor);
                        if (!seen.Add(moved.Position)) continue;

                        if (!pushes.Any(p => Overlaps(moved, p.Zone)))
                        {
                            var cost = Cost(window.Position, moved.Position);
                            if (cost < bestCost) { best = moved.Position; bestCost = cost; }
                        }
                        else
                        {
                            next.Add(moved);
                        }
                    }
                }
            }
            frontier = next;
        }
        return best;
    }

    private static readonly ScreenEdge[] Edges = { ScreenEdge.Top, ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right };

    /// <summary>Direction a zone pushes the window: away from the edge it is attached to.</summary>
    private static ScreenEdge[] PushDirections(PixelRect zone, IReadOnlyList<PixelRect> monitors)
        => Classify(zone, monitors) is { } chrome ? new[] { chrome.Edge } : Edges;

    /// <summary>
    /// Moves the window just past the zone. "Edge" names the side the zone sits
    /// on, so a bottom zone moves the window up above it.
    /// </summary>
    private static PixelRect Push(PixelRect window, PixelRect zone, ScreenEdge zoneSide, int margin) => zoneSide switch
    {
        ScreenEdge.Bottom => window.WithY(zone.Y - margin - window.Height),
        ScreenEdge.Top => window.WithY(zone.Bottom + margin),
        ScreenEdge.Left => window.WithX(zone.Right + margin),
        ScreenEdge.Right => window.WithX(zone.X - margin - window.Width),
        _ => window,
    };

    private static PixelRect Clamp(PixelRect window, PixelRect monitor)
    {
        // A window larger than the monitor pins to its top-left; nothing better exists.
        var x = Math.Max(monitor.X, Math.Min(window.X, monitor.Right - window.Width));
        var y = Math.Max(monitor.Y, Math.Min(window.Y, monitor.Bottom - window.Height));
        return new PixelRect(x, y, window.Width, window.Height);
    }

    // Manhattan distance: matches how far the user's eye has to follow the window.
    private static long Cost(PixelPoint from, PixelPoint to) => (long)Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);

    /// <summary>Positive-area intersection. Rects that only touch don't overlap.</summary>
    internal static bool Overlaps(PixelRect a, PixelRect b)
        => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static bool IsFlush(PixelRect rect, PixelRect monitor, ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top => Math.Abs(rect.Y - monitor.Y) <= EdgeTolerance,
        ScreenEdge.Bottom => Math.Abs(rect.Bottom - monitor.Bottom) <= EdgeTolerance,
        ScreenEdge.Left => Math.Abs(rect.X - monitor.X) <= EdgeTolerance,
        ScreenEdge.Right => Math.Abs(rect.Right - monitor.Right) <= EdgeTolerance,
        _ => false,
    };

    /// <summary>
    /// The monitor sharing the most area with the rect. Falls back to the
    /// nearest monitor for a rect entirely off-screen, so a window dropped past
    /// the edge still has a home to be clamped into.
    /// </summary>
    internal static PixelRect? FindMonitor(PixelRect rect, IReadOnlyList<PixelRect> monitors)
    {
        if (FindOverlappingMonitor(rect, monitors) is { } overlapping) return overlapping;

        var center = rect.Center;
        return monitors.Count == 0
            ? null
            : monitors.MinBy(m => DistanceSquared(center, Nearest(m, center)));
    }

    private static PixelRect? FindOverlappingMonitor(PixelRect rect, IReadOnlyList<PixelRect> monitors)
    {
        PixelRect? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var overlap = rect.Intersect(monitor);
            long area = (long)overlap.Width * overlap.Height;
            if (area > bestArea) { best = monitor; bestArea = area; }
        }
        return best;
    }

    private static PixelPoint Nearest(PixelRect r, PixelPoint p)
        => new(Math.Clamp(p.X, r.X, r.Right - 1), Math.Clamp(p.Y, r.Y, r.Bottom - 1));

    private static long DistanceSquared(PixelPoint a, PixelPoint b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
