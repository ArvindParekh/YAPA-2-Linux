using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tmds.DBus.Protocol;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Platform.Linux.DBus;

namespace YAPA.Avalonia.Tests.DBus;

/// <summary>Runs against a private dbus-daemon and a fake notification server. Nothing reaches the desktop.</summary>
[TestClass]
public class DBusNotifierTests
{
    private static readonly DBusPolicy FastPolicy = new()
    {
        CallTimeout = TimeSpan.FromSeconds(2),
        InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
    };

    private static readonly DesktopNotification BreakDue = new(
        "Pomodoro complete", "Time for a 5-minute break",
        [new NotificationAction("start-break", "Start break"), new NotificationAction("skip-break", "Skip break")]);

    [TestMethod, Timeout(20000)]
    public async Task Show_sends_spec_arguments_for_a_transient_notification()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await notifier.ShowAsync(BreakDue);

        var call = server.NotifyCalls.Single();
        Assert.AreEqual("YAPA 2", call.AppName);
        Assert.AreEqual(0u, call.ReplacesId);
        Assert.AreEqual("yapa2", call.AppIcon);
        Assert.AreEqual("Pomodoro complete", call.Summary);
        Assert.AreEqual("Time for a 5-minute break", call.Body);
        CollectionAssert.AreEqual(new[] { "start-break", "Start break", "skip-break", "Skip break" }, call.Actions);
        Assert.AreEqual(-1, call.ExpireTimeout);

        Assert.AreEqual(VariantValueType.Byte, call.Hints["urgency"].Type);
        Assert.AreEqual((byte)1, call.Hints["urgency"].GetByte());
        Assert.AreEqual("yapa2", call.Hints["desktop-entry"].GetString());
        Assert.AreEqual("x-yapa.timer", call.Hints["category"].GetString());
        Assert.IsTrue(call.Hints["transient"].GetBool());
        Assert.IsFalse(call.Hints.ContainsKey("resident"), "resident would keep a clicked notification around in GNOME");
        Assert.AreEqual(1u, notifier.CurrentNotificationId);
    }

    [TestMethod, Timeout(20000)]
    public async Task Persistent_notification_is_not_transient_and_never_expires()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await notifier.ShowAsync(BreakDue with { Persistent = true, IconName = null, Category = "x-test" });

        var call = server.NotifyCalls.Single();
        Assert.IsFalse(call.Hints["transient"].GetBool());
        Assert.AreEqual(0, call.ExpireTimeout);
        Assert.AreEqual("", call.AppIcon);
        Assert.AreEqual("x-test", call.Hints["category"].GetString());
    }

    [TestMethod, Timeout(20000)]
    public async Task Body_is_escaped_only_when_the_server_parses_markup()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        var plain = BreakDue with { Title = "Q&A <draft>", Body = "Fix <b> & ship" };

        using (var server = await FakeNotificationServer.StartAsync(bus.Address, ["actions", "body", "body-markup"]))
        using (var notifier = new DBusNotifier(bus.Address, FastPolicy))
        {
            await notifier.ShowAsync(plain);
            Assert.AreEqual("Fix &lt;b&gt; &amp; ship", server.NotifyCalls.Single().Body);
            Assert.AreEqual("Q&A <draft>", server.NotifyCalls.Single().Summary, "summary is never markup");
        }

        using (var server = await FakeNotificationServer.StartAsync(bus.Address, ["actions", "body"]))
        using (var notifier = new DBusNotifier(bus.Address, FastPolicy))
        {
            await notifier.ShowAsync(plain);
            Assert.AreEqual("Fix <b> & ship", server.NotifyCalls.Single().Body);
        }
    }

    [TestMethod, Timeout(20000)]
    public async Task Each_show_replaces_the_previous_one()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address, firstId: 41);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await notifier.ShowAsync(BreakDue);
        await notifier.ShowAsync(BreakDue with { Title = "Second" });
        await notifier.ShowAsync(BreakDue with { Title = "Third" });

        CollectionAssert.AreEqual(new[] { 0u, 41u, 41u }, server.NotifyCalls.Select(c => c.ReplacesId).ToArray());
    }

    [TestMethod, Timeout(20000)]
    public async Task Concurrent_shows_are_serialised_so_they_still_replace()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address, firstId: 7);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => notifier.ShowAsync(BreakDue with { Title = $"#{i}" })));

        var replaces = server.NotifyCalls.Select(c => c.ReplacesId).ToArray();
        Assert.AreEqual(5, replaces.Length);
        Assert.AreEqual(1, replaces.Count(r => r == 0), "only the first call may create a new notification");
        Assert.IsTrue(replaces.Where(r => r != 0).All(r => r == 7));
    }

    [TestMethod, Timeout(20000)]
    public async Task After_the_server_closes_it_the_next_show_starts_fresh()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await notifier.ShowAsync(BreakDue);
        server.EmitNotificationClosed(999); // someone else's
        server.EmitNotificationClosed(1);
        await Poll.UntilAsync(() => notifier.CurrentNotificationId == 0, "NotificationClosed to clear our id");

        await notifier.ShowAsync(BreakDue);
        Assert.AreEqual(0u, server.NotifyCalls[1].ReplacesId);
    }

    [TestMethod, Timeout(20000)]
    public async Task Dismiss_closes_the_current_notification_only()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address, firstId: 5);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        await notifier.DismissAsync(); // nothing shown yet: must not call the server
        Assert.AreEqual(0, server.CloseCalls.Count);

        await notifier.ShowAsync(BreakDue);
        await notifier.DismissAsync();
        CollectionAssert.AreEqual(new[] { 5u }, server.CloseCalls.ToArray());
        Assert.AreEqual(0u, notifier.CurrentNotificationId);

        await notifier.DismissAsync();
        Assert.AreEqual(1, server.CloseCalls.Count, "second dismiss has nothing to close");

        await notifier.ShowAsync(BreakDue);
        Assert.AreEqual(0u, server.NotifyCalls[1].ReplacesId);
    }

    [TestMethod, Timeout(20000)]
    public async Task Action_on_our_notification_is_raised_and_others_are_ignored()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address, firstId: 3);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);
        var actions = new EventRecorder<string>();
        notifier.ActionInvoked += actions.Record;

        await notifier.ShowAsync(BreakDue);
        // Signals from one sender arrive in order, so if the foreign one weren't filtered it
        // would be dequeued first.
        server.EmitActionInvoked(77, "someone-elses");
        server.EmitActionInvoked(3, "start-break");
        Assert.AreEqual("start-break", await actions.NextAsync());

        server.EmitActionInvoked(3, "default");
        Assert.AreEqual("default", await actions.NextAsync());
        Assert.AreEqual(0, actions.Count);
    }

    [TestMethod, Timeout(20000)]
    public async Task Action_after_dismiss_is_ignored()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address, firstId: 3);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);
        var actions = new EventRecorder<string>();
        notifier.ActionInvoked += actions.Record;

        await notifier.ShowAsync(BreakDue);
        await notifier.DismissAsync();
        server.EmitActionInvoked(3, "start-break");

        await notifier.ShowAsync(BreakDue); // new id 4
        server.EmitActionInvoked(4, "skip-break");
        Assert.AreEqual("skip-break", await actions.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task A_throwing_subscriber_does_not_break_later_actions()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);
        var actions = new EventRecorder<string>();
        notifier.ActionInvoked += key =>
        {
            actions.Record(key);
            throw new InvalidOperationException("consumer bug");
        };

        await notifier.ShowAsync(BreakDue);
        server.EmitActionInvoked(1, "a");
        server.EmitActionInvoked(1, "b");
        Assert.AreEqual("a", await actions.NextAsync());
        Assert.AreEqual("b", await actions.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Capabilities_are_detected_on_connect()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using (var server = await FakeNotificationServer.StartAsync(bus.Address, ["body", "actions", "persistence"]))
        using (var notifier = new DBusNotifier(bus.Address, FastPolicy))
        {
            Assert.IsFalse(notifier.SupportsActions, "false until connected");
            await notifier.ShowAsync(BreakDue);
            Assert.IsTrue(notifier.SupportsActions);
            CollectionAssert.AreEqual(new[] { "body", "actions", "persistence" }, notifier.Capabilities.ToArray());
        }

        using (var server = await FakeNotificationServer.StartAsync(bus.Address, ["body"]))
        using (var notifier = new DBusNotifier(bus.Address, FastPolicy))
        {
            await notifier.ShowAsync(BreakDue);
            Assert.IsFalse(notifier.SupportsActions);
            Assert.AreEqual(1, server.NotifyCalls.Count, "still shows the notification, just without buttons");
        }
    }

    [TestMethod, Timeout(20000)]
    public async Task Server_that_appears_later_is_picked_up()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);

        // Like autostarting before gnome-shell owns the name.
        await notifier.ShowAsync(BreakDue);
        Assert.IsFalse(notifier.SupportsActions);

        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        await notifier.ShowAsync(BreakDue);
        Assert.IsTrue(notifier.SupportsActions);
        Assert.AreEqual(1, server.NotifyCalls.Count);
    }

    [TestMethod, Timeout(20000)]
    public async Task Server_restart_resets_id_and_capabilities()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);
        var first = await FakeNotificationServer.StartAsync(bus.Address, firstId: 10);
        await notifier.ShowAsync(BreakDue);
        Assert.IsTrue(notifier.SupportsActions);
        Assert.AreEqual(10u, notifier.CurrentNotificationId);

        first.Dispose();
        await Poll.UntilAsync(() => notifier.CurrentNotificationId == 0, "NameOwnerChanged to reset our id");

        using var second = await FakeNotificationServer.StartAsync(bus.Address, ["body"], firstId: 1);
        await notifier.ShowAsync(BreakDue);
        Assert.AreEqual(0u, second.NotifyCalls.Single().ReplacesId);
        Assert.IsFalse(notifier.SupportsActions, "capabilities re-queried from the new server");
    }

    [TestMethod, Timeout(30000)]
    public async Task Reconnects_after_the_bus_connection_drops()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var notifier = new DBusNotifier(bus.Address, FastPolicy);
        var actions = new EventRecorder<string>();
        notifier.ActionInvoked += actions.Record;
        var first = await FakeNotificationServer.StartAsync(bus.Address);
        await notifier.ShowAsync(BreakDue);

        bus.Restart();
        first.Dispose();
        using var second = await FakeNotificationServer.StartAsync(bus.Address, firstId: 20);

        // Show is never retried internally, so keep showing until the reconnect lands.
        await Poll.UntilAsync(async () =>
        {
            await notifier.ShowAsync(BreakDue);
            return second.NotifyCalls.Count > 0;
        }, "notifier to reach the restarted bus");

        // Subscriptions must have been re-installed on the new connection.
        Assert.AreEqual(20u, notifier.CurrentNotificationId);
        second.EmitActionInvoked(20, "start-break");
        Assert.AreEqual("start-break", await actions.NextAsync());
    }

    [TestMethod, Timeout(20000)]
    public async Task Hung_server_does_not_hang_the_caller()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        var policy = FastPolicy with { CallTimeout = TimeSpan.FromMilliseconds(300) };
        using var notifier = new DBusNotifier(bus.Address, policy);
        await notifier.ShowAsync(BreakDue);
        server.HangNotify = true;

        var watch = Stopwatch.StartNew();
        await notifier.ShowAsync(BreakDue);
        Assert.IsTrue(watch.ElapsedMilliseconds < 2000, $"ShowAsync took {watch.ElapsedMilliseconds} ms");

        // The serial gate must be released after the inner timeout, or every later call would queue forever.
        server.HangNotify = false;
        await notifier.ShowAsync(BreakDue);
        await Poll.UntilAsync(() => server.NotifyCalls.Count == 3, "third Notify after the hung one");
    }

    [TestMethod, Timeout(20000)]
    public async Task Missing_bus_is_a_quiet_no_op()
    {
        // No daemon needed: the socket doesn't exist.
        using var notifier = new DBusNotifier("unix:path=/tmp/yapa-no-such-bus-" + Guid.NewGuid().ToString("N"), FastPolicy);
        var watch = Stopwatch.StartNew();
        await notifier.ShowAsync(BreakDue);
        await notifier.DismissAsync();
        Assert.IsFalse(notifier.SupportsActions);
        Assert.IsTrue(watch.ElapsedMilliseconds < 3000);

        using var noAddress = new DBusNotifier(null, FastPolicy);
        await noAddress.ShowAsync(BreakDue);
        await noAddress.ShowAsync(null!);
    }

    [TestMethod, Timeout(20000)]
    public async Task Calls_after_dispose_are_ignored()
    {
        using var bus = PrivateBus.StartOrInconclusive();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        var notifier = new DBusNotifier(bus.Address, FastPolicy);
        await notifier.ShowAsync(BreakDue);
        notifier.Dispose();
        notifier.Dispose();

        await notifier.ShowAsync(BreakDue);
        await notifier.DismissAsync();
        Assert.AreEqual(1, server.NotifyCalls.Count);
    }
}
