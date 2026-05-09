using System;
using Avalonia.Threading;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public class AvaloniaTimer : ITimer
{
    private DispatcherTimer? _timer;

    // Lazy init so DispatcherTimer is created on the UI thread after Avalonia starts.
    private DispatcherTimer GetTimer()
    {
        if (_timer != null)
            return _timer;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick?.Invoke();
        return _timer;
    }

    public event Action? Tick;

    public void Start() => GetTimer().Start();
    public void Stop() => _timer?.Stop();
}
