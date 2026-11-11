using System;
using Avalonia.Threading;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaTimer : ITimer
{
    // Used only until the engine asks for a specific instant (it does after every tick,
    // aiming just past the next second boundary).
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(1.1);

    private readonly TimeSpan _longest;
    private DispatcherTimer? _timer;

    /// <param name="longest">
    /// The longest wait <see cref="ScheduleNext"/> honours. The default suits the engine's
    /// one-second cadence; something that only needs to wake now and then asks for more.
    /// </param>
    public AvaloniaTimer(TimeSpan? longest = null) => _longest = longest ?? Longest;

    // YAPA_TRACE_TIMING=1 logs how late each scheduled tick actually fired.
    private static readonly bool TraceTiming = Environment.GetEnvironmentVariable("YAPA_TRACE_TIMING") == "1";
    private TimeSpan _dueAt;

    public event Action? Tick;

    public void Start()
    {
        // Created lazily so it binds to the UI dispatcher once Avalonia is running.
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled)
        {
            _timer.Interval = DefaultInterval;
            _timer.Start();
        }
    }

    public void Stop() => _timer?.Stop();

    public void ScheduleNext(TimeSpan delay)
    {
        if (_timer is not { IsEnabled: true } timer) return;
        // Setting Interval restarts the countdown from now, which is exactly the intent.
        timer.Interval = delay < Shortest ? Shortest : delay > _longest ? _longest : delay;
        if (TraceTiming) _dueAt = System.Diagnostics.Stopwatch.GetElapsedTime(0) + timer.Interval;
    }

    private DispatcherTimer CreateTimer()
    {
        // Render priority: the second flip is the most visible thing the app does, so it
        // shouldn't wait behind background work.
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = DefaultInterval };
        timer.Tick += (_, _) =>
        {
            if (TraceTiming && _dueAt > TimeSpan.Zero)
                System.Diagnostics.Trace.WriteLine($"[timing] tick {(System.Diagnostics.Stopwatch.GetElapsedTime(0) - _dueAt).TotalMilliseconds:+0.0;-0.0;0.0} ms vs schedule");
            Tick?.Invoke();
        };
        return timer;
    }
}
