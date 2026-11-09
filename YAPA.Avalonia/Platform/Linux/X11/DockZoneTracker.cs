using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Remembers the shell chrome it has seen, one zone per (monitor, edge).
///
/// The live input region only shows an autohide dock while the dock is out.
/// Avoidance must also hold while the dock is hidden: if the user drops the
/// timer where the hidden dock lives, the next time the dock slides in it
/// swallows the timer's clicks. So a zone outlives its disappearance from the
/// region.
///
/// Observations of the same edge on the same monitor update each other rather
/// than piling up, so a dock that shrinks when an app closes doesn't leave its
/// old, longer footprint behind. A thinner observation is treated with
/// suspicion: a dock sliding in or out passes through thinner frames, and those
/// must not replace the real footprint. A thinner reading only replaces the
/// remembered zone once it has held steady for <see cref="SettleTime"/> (e.g.
/// after an icon-size change).
///
/// Not thread-safe. Drive it from one thread (the UI thread) and marshal the
/// monitor's background-thread events onto it.
/// </summary>
public sealed class DockZoneTracker
{
    /// <summary>Thickness changes within this tolerance count as "the same size", not as shrinking.</summary>
    private const int ThicknessTolerance = DockAvoidance.EdgeTolerance;

    private readonly Dictionary<(PixelRect Monitor, ScreenEdge Edge), Entry> _entries = new();

    public DockZoneTracker(TimeSpan? settleTime = null)
    {
        SettleTime = settleTime ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>How long a thinner reading must stay put before it replaces the remembered zone.</summary>
    public TimeSpan SettleTime { get; }

    /// <summary>
    /// Feeds one snapshot of the shell's input region.
    /// </summary>
    /// <param name="regionRects">Region rects in root coordinates.</param>
    /// <param name="monitors">Current monitor bounds. Zones on monitors that are gone are dropped.</param>
    /// <param name="now">Monotonic timestamp (e.g. <c>Stopwatch.GetElapsedTime</c>), used only to settle thinner readings.</param>
    /// <returns>True when the remembered zones changed.</returns>
    public bool Observe(IEnumerable<PixelRect> regionRects, IReadOnlyList<PixelRect> monitors, TimeSpan now)
    {
        var changed = ForgetMissingMonitors(monitors);

        // One snapshot can carry several rects for the same edge (e.g. a panel
        // split around a popup). Their union is that edge's footprint now.
        var snapshot = DockAvoidance.ClassifyAll(regionRects, monitors)
            .GroupBy(z => (z.Monitor, z.Edge))
            .ToDictionary(g => g.Key, g => g.Aggregate((a, b) => a with { Bounds = a.Bounds.Union(b.Bounds) }));

        foreach (var (key, entry) in _entries)
        {
            // The edge vanished from the region (the dock hid). Keep the zone,
            // but a pending thinner reading was only an animation frame.
            if (!snapshot.ContainsKey(key)) entry.Pending = null;
        }

        foreach (var (key, zone) in snapshot)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = new Entry(zone);
                changed = true;
            }
            else if (zone.Thickness >= entry.Zone.Thickness - ThicknessTolerance)
            {
                // Same depth or deeper: this is the chrome fully shown. Take it
                // as is, which also lets the length grow or shrink.
                changed |= entry.Zone != zone;
                entry.Zone = zone;
                entry.Pending = null;
            }
            else if (entry.Pending is not { } pending || pending.Zone != zone)
            {
                entry.Pending = (zone, now);
            }
        }

        return Settle(now) | changed;
    }

    /// <summary>Zones to avoid at <paramref name="now"/>, including chrome that is currently hidden.</summary>
    public IReadOnlyList<ChromeZone> GetZones(TimeSpan now)
    {
        Settle(now);
        return _entries.Values.Select(e => e.Zone).ToList();
    }

    /// <summary>Forgets everything, e.g. after the user changes dock settings.</summary>
    public void Clear() => _entries.Clear();

    private bool Settle(TimeSpan now)
    {
        var changed = false;
        foreach (var entry in _entries.Values)
        {
            if (entry.Pending is { } pending && now - pending.Since >= SettleTime)
            {
                entry.Zone = pending.Zone;
                entry.Pending = null;
                changed = true;
            }
        }
        return changed;
    }

    private bool ForgetMissingMonitors(IReadOnlyList<PixelRect> monitors)
    {
        var stale = _entries.Keys.Where(k => !monitors.Contains(k.Monitor)).ToList();
        foreach (var key in stale) _entries.Remove(key);
        return stale.Count > 0;
    }

    private sealed class Entry
    {
        public Entry(ChromeZone zone) => Zone = zone;
        public ChromeZone Zone { get; set; }
        public (ChromeZone Zone, TimeSpan Since)? Pending { get; set; }
    }
}
