using System;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Platform;

/// <summary>Used off Linux or when there is no session bus.</summary>
public sealed class NullDesktopNotifier : IDesktopNotifier
{
    public bool SupportsActions => false;

    public Task ShowAsync(DesktopNotification notification) => Task.CompletedTask;

    public Task DismissAsync() => Task.CompletedTask;

    public event Action<string>? ActionInvoked { add { } remove { } }

    public void Dispose() { }
}

/// <summary>Used off Linux. Never raises events.</summary>
public sealed class NullSessionMonitor : ISessionMonitor
{
    public Task StartAsync() => Task.CompletedTask;

    public event Action<bool>? ScreenLockedChanged { add { } remove { } }

    public event Action<bool>? SleepingChanged { add { } remove { } }

    public void Dispose() { }
}
