using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.DBus;

namespace YAPA.Avalonia.Tests.DBus;

/// <summary>
/// Fake ScreenSaver and logind services on private buses. A second private bus stands in for
/// the system bus so the two can fail independently.
/// </summary>
[TestClass]
public class DBusSessionMonitorTests
{
    private static readonly DBusPolicy FastPolicy = new()
    {
        CallTimeout = TimeSpan.FromSeconds(2),
        InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
    };

    private const string NoSuchBus = "unix:path=/tmp/yapa-no-such-bus";

    [TestMethod, Timeout(20000)]
    public async Task Gnome_screensaver_reports_lock_and_unlock()
    {
        using var session = PrivateBus.StartOrInconclusive();
        using var screensaver = await FakeService.OwnAsync(session.Address, "org.gnome.ScreenSaver");
        using var monitor = new DBusSessionMonitor(session.Address, NoSuchBus, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        screensaver.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
        screensaver.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", false);
        Assert.IsFalse(await locks.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Freedesktop_screensaver_is_used_on_any_path()
    {
        using var session = PrivateBus.StartOrInconclusive();
        using var screensaver = await FakeService.OwnAsync(session.Address, "org.freedesktop.ScreenSaver");
        using var monitor = new DBusSessionMonitor(session.Address, null, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        screensaver.EmitBool("/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
        screensaver.EmitBool("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", false);
        Assert.IsFalse(await locks.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Lock_sources_are_merged_by_state()
    {
        using var session = PrivateBus.StartOrInconclusive();
        using var system = PrivateBus.StartOrInconclusive();
        using var gnome = await FakeService.OwnAsync(session.Address, "org.gnome.ScreenSaver");
        using var freedesktop = await FakeService.OwnAsync(session.Address, "org.freedesktop.ScreenSaver");
        using var login1 = await FakeLogin1.StartAsync(system.Address);
        using var monitor = new DBusSessionMonitor(session.Address, system.Address, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        // A GNOME lock: shell shield, the gsd proxy re-emitting it, and logind's LockedHint.
        gnome.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
        freedesktop.EmitBool("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", true);
        login1.EmitLockedHint(true);
        // Signals from different connections have no relative order, so let the duplicates
        // land before the next real transition.
        await Task.Delay(200);
        Assert.AreEqual(0, locks.Count, "duplicate lock reports must be dropped");

        login1.EmitLockedHint(false);
        Assert.IsFalse(await locks.NextAsync());
        gnome.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", false);
        freedesktop.EmitBool("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", false);
        await Task.Delay(200);
        Assert.AreEqual(0, locks.Count, "duplicate unlock reports must be dropped");

        gnome.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Signals_from_a_non_owner_are_ignored()
    {
        using var session = PrivateBus.StartOrInconclusive();
        using var screensaver = await FakeService.OwnAsync(session.Address, "org.gnome.ScreenSaver");
        using var impostor = await FakeService.OwnAsync(session.Address, "org.example.Impostor");
        using var monitor = new DBusSessionMonitor(session.Address, null, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        impostor.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", false);
        screensaver.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
        await Task.Delay(200);
        Assert.AreEqual(0, locks.Count);
    }

    [TestMethod, Timeout(20000)]
    public async Task PrepareForSleep_reports_suspend_and_resume()
    {
        using var system = PrivateBus.StartOrInconclusive();
        using var login1 = await FakeLogin1.StartAsync(system.Address);
        using var monitor = new DBusSessionMonitor(NoSuchBus, system.Address, FastPolicy);
        var sleeps = new EventRecorder<bool>();
        monitor.SleepingChanged += sleeps.Record;
        await monitor.StartAsync();

        login1.EmitPrepareForSleep(true);
        Assert.IsTrue(await sleeps.NextAsync());
        login1.EmitPrepareForSleep(false);
        Assert.IsFalse(await sleeps.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task LockedHint_is_watched_on_the_resolved_session()
    {
        using var system = PrivateBus.StartOrInconclusive();
        using var login1 = await FakeLogin1.StartAsync(system.Address);
        using var monitor = new DBusSessionMonitor(null, system.Address, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        var expectedId = Environment.GetEnvironmentVariable("XDG_SESSION_ID") is { Length: > 0 } id ? id : "auto-resolved";
        CollectionAssert.AreEqual(new[] { expectedId }, login1.RequestedSessionIds.ToArray());

        login1.EmitLockedHint(true);
        Assert.IsTrue(await locks.NextAsync());
        login1.EmitLockedHint(false);
        Assert.IsFalse(await locks.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Missing_system_bus_does_not_affect_session_sources()
    {
        using var session = PrivateBus.StartOrInconclusive();
        using var screensaver = await FakeService.OwnAsync(session.Address, "org.gnome.ScreenSaver");
        using var monitor = new DBusSessionMonitor(session.Address, NoSuchBus, FastPolicy);
        var locks = new EventRecorder<bool>();
        monitor.ScreenLockedChanged += locks.Record;
        await monitor.StartAsync();

        screensaver.EmitBool("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", true);
        Assert.IsTrue(await locks.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Failed_session_lookup_does_not_affect_PrepareForSleep()
    {
        using var system = PrivateBus.StartOrInconclusive();
        using var login1 = await FakeLogin1.StartAsync(system.Address, supportsSessions: false);
        using var monitor = new DBusSessionMonitor(null, system.Address, FastPolicy);
        var sleeps = new EventRecorder<bool>();
        monitor.SleepingChanged += sleeps.Record;
        await monitor.StartAsync();

        login1.EmitPrepareForSleep(true);
        Assert.IsTrue(await sleeps.NextAsync());
    }

    [TestMethod, Timeout(30000)]
    public async Task Resubscribes_after_the_system_bus_restarts()
    {
        using var system = PrivateBus.StartOrInconclusive();
        var first = await FakeLogin1.StartAsync(system.Address);
        using var monitor = new DBusSessionMonitor(null, system.Address, FastPolicy);
        var sleeps = new EventRecorder<bool>();
        monitor.SleepingChanged += sleeps.Record;
        await monitor.StartAsync();

        system.Restart();
        first.Dispose();
        using var second = await FakeLogin1.StartAsync(system.Address);

        // The monitor reconnects on its own; keep emitting until the new match rule is in place.
        await Poll.UntilAsync(async () =>
        {
            second.EmitPrepareForSleep(true);
            await Task.Delay(50);
            return sleeps.Count > 0;
        }, "monitor to resubscribe on the restarted bus");
        Assert.IsTrue(await sleeps.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Start_without_any_bus_completes_quietly()
    {
        using var monitor = new DBusSessionMonitor(null, NoSuchBus, FastPolicy);
        await monitor.StartAsync();
        await monitor.StartAsync();
    }
}
