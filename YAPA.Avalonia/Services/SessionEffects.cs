using System;
using System.Diagnostics;
using Avalonia.Threading;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Optional "pause while the screen is locked". Sleep needs no handling: the
/// engine measures time on a clock that stops during suspend.
/// </summary>
public sealed class SessionEffects : IDisposable
{
    private readonly IPomodoroEngine _engine;
    private readonly GeneralSettings _general;
    private readonly ISessionMonitor _monitor;
    private bool _pausedByLock;

    public SessionEffects(IPomodoroEngine engine, GeneralSettings general, ISessionMonitor monitor)
    {
        _engine = engine;
        _general = general;
        _monitor = monitor;
        _monitor.ScreenLockedChanged += OnLockChanged;
        _ = _monitor.StartAsync();
    }

    private void OnLockChanged(bool locked) => Dispatcher.UIThread.Post(() =>
    {
        if (locked)
        {
            if (!_general.PauseWhenLocked || !_engine.IsRunning) return;
            _engine.Pause();
            _pausedByLock = true;
            Trace.WriteLine("[session] screen locked: paused");
        }
        else if (_pausedByLock)
        {
            // Resume only what we paused; if the user stopped or changed
            // anything in between, leave it alone.
            _pausedByLock = false;
            if (_engine.Phase == PomodoroPhase.Pause)
            {
                _engine.Start();
                Trace.WriteLine("[session] screen unlocked: resumed");
            }
        }
    });

    public void Dispose()
    {
        _monitor.ScreenLockedChanged -= OnLockChanged;
        _monitor.Dispose();
    }
}
