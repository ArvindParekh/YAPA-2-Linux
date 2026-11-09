using System;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Reports screen lock and suspend/resume. Events are raised on a background thread;
/// consumers marshal to the UI thread.
/// </summary>
public interface ISessionMonitor : IDisposable
{
    /// <summary>
    /// Subscribes to the session's lock and sleep sources. Never throws; a source that is
    /// unavailable is logged and skipped without affecting the others. Calling it again is a no-op.
    /// </summary>
    Task StartAsync();

    /// <summary>True = locked. Raised only when the state changes, however many sources report it.</summary>
    event Action<bool>? ScreenLockedChanged;

    /// <summary>True = about to suspend, false = resumed.</summary>
    event Action<bool>? SleepingChanged;
}
