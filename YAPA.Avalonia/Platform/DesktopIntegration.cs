using System;
using System.Diagnostics;
using Tmds.DBus.Protocol;
using YAPA.Avalonia.Platform.Linux.DBus;

namespace YAPA.Avalonia.Platform;

/// <summary>Picks the desktop integration for the current OS. Nothing connects until first use.</summary>
public static class DesktopIntegration
{
    public static IDesktopNotifier CreateNotifier()
    {
        if (!OperatingSystem.IsLinux())
            return new NullDesktopNotifier();

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            Trace.WriteLine("[dbus] no session bus address; desktop notifications disabled");
            return new NullDesktopNotifier();
        }
        return new DBusNotifier(address);
    }

    public static ISessionMonitor CreateSessionMonitor()
    {
        if (!OperatingSystem.IsLinux())
            return new NullSessionMonitor();

        // Either bus may be missing (containers, minimal sessions); the monitor handles a null
        // address per bus so the other one still works.
        return new DBusSessionMonitor(DBusAddress.Session, DBusAddress.System);
    }
}
