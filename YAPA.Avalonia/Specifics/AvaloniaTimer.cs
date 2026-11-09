using System;
using Avalonia.Threading;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaTimer : ITimer
{
    // Several ticks per second: the displayed second then flips within ~1/4 s of
    // the real boundary, where a 1 s timer drifts against it and occasionally
    // skips a digit. The engine only raises changes when the second changes.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? _timer;

    public event Action? Tick;

    public void Start()
    {
        // Created lazily so it binds to the UI dispatcher once Avalonia is running.
        _timer ??= CreateTimer();
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Interval };
        timer.Tick += (_, _) => Tick?.Invoke();
        return timer;
    }
}
