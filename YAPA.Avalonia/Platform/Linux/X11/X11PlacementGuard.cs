using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Threading;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Keeps the timer out of GNOME Shell's click-eating panels on X11.
///
/// The shell's input region updates at frame rate while the dock slides, so
/// raw changes are coalesced onto the UI thread at most every
/// <see cref="Throttle"/>, with one trailing re-check after the tracker's
/// settle time, so a dock that ends up thinner is noticed once it has stopped moving.
/// </summary>
public sealed class X11PlacementGuard : IWindowPlacementGuard, IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(120);

    private readonly IShellInputRegionMonitor _monitor;
    private readonly DockZoneTracker _tracker = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _settle;
    private IReadOnlyList<PixelRect> _monitors = [];

    public X11PlacementGuard(IShellInputRegionMonitor monitor)
    {
        _monitor = monitor;
        _throttle = new DispatcherTimer { Interval = Throttle };
        _throttle.Tick += (_, _) => { _throttle.Stop(); Observe(); };
        _settle = new DispatcherTimer { Interval = _tracker.SettleTime + TimeSpan.FromMilliseconds(200) };
        _settle.Tick += (_, _) => { _settle.Stop(); Observe(); };

        _monitor.Changed += OnRegionChanged;
        _monitor.Start();
    }

    public event Action? ZonesChanged;

    public PixelPoint? Resolve(PixelRect window, IReadOnlyList<PixelRect> monitors)
    {
        _monitors = monitors;
        _tracker.Observe(_monitor.Current, monitors, Now);
        var zones = _tracker.GetZones(Now).Select(z => z.Bounds).ToList();
        var nudge = DockAvoidance.ComputeNudge(window, zones, monitors);
        if (nudge is { } p)
            Trace.WriteLine($"[placement] {window} overlaps shell panels; moving to {p}");
        return nudge;
    }

    public void Prime(IReadOnlyList<PixelRect> monitors)
    {
        // Right after startup the monitor thread may not have read the region yet.
        // A few milliseconds normally; bounded so a slow X server can't stall showing the window.
        if (_monitor is X11ShellInputRegionMonitor x11) x11.WaitForFirstSnapshot(TimeSpan.FromMilliseconds(300));
        _monitors = monitors;
        _tracker.Observe(_monitor.Current, monitors, Now);
    }

    private static TimeSpan Now => Stopwatch.GetElapsedTime(0);

    // Raised on the monitor's X thread.
    private void OnRegionChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_throttle.IsEnabled) _throttle.Start();
        _settle.Stop();
        _settle.Start();
    });

    private void Observe()
    {
        if (_monitors.Count == 0) return; // no window has reported its screens yet
        if (_tracker.Observe(_monitor.Current, _monitors, Now))
            ZonesChanged?.Invoke();
    }

    public void Dispose()
    {
        _monitor.Changed -= OnRegionChanged;
        _throttle.Stop();
        _settle.Stop();
        _monitor.Dispose();
    }

    /// <summary>The guard for this session: X11 when available, otherwise one that never moves anything.</summary>
    public static IWindowPlacementGuard Create()
    {
        var monitor = ShellInputRegionMonitor.Create();
        return monitor is NullShellInputRegionMonitor ? new NullPlacementGuard() : new X11PlacementGuard(monitor);
    }
}
