using System;
using System.Diagnostics;

namespace YAPA.Avalonia.Platform.Linux.DBus;

/// <summary>Timing knobs for D-Bus clients. Tests shrink them; production uses <see cref="Default"/>.</summary>
public sealed record DBusPolicy
{
    public static DBusPolicy Default { get; } = new();

    /// <summary>
    /// Upper bound on how long a caller waits for a connect or method call. The D-Bus default is
    /// 25 s, far too long for a timer that has to keep ticking.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>First retry after a connection drops; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(60);
}

internal static class DBusLog
{
    public static void Write(string message) => Trace.WriteLine("[dbus] " + message);
}
