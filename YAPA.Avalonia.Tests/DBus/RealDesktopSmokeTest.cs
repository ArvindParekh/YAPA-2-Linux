using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tmds.DBus.Protocol;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Platform.Linux.DBus;

namespace YAPA.Avalonia.Tests.DBus;

/// <summary>
/// Shows one real notification on the user's desktop to check the action buttons render.
/// Opt-in only, because it is visible: <c>YAPA_DBUS_SMOKE=1 dotnet test --filter RealDesktop</c>.
/// </summary>
[TestClass]
public class RealDesktopSmokeTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("RealDesktop"), Timeout(30000)]
    public async Task Shows_a_transient_notification_with_buttons_on_the_real_session_bus()
    {
        if (Environment.GetEnvironmentVariable("YAPA_DBUS_SMOKE") != "1")
            Assert.Inconclusive("set YAPA_DBUS_SMOKE=1 to show a real notification");
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
            Assert.Inconclusive("no session bus");

        using var notifier = new DBusNotifier(address);
        await notifier.ShowAsync(new DesktopNotification(
            "YAPA 2 (developer test)",
            "Checking that action buttons render. Closes itself in 4 seconds.",
            [new NotificationAction("start-break", "Start break"), new NotificationAction("skip-break", "Skip break")],
            Persistent: false));

        var capabilities = string.Join(", ", notifier.Capabilities);
        TestContext.WriteLine($"GetCapabilities: {capabilities}");
        Console.WriteLine($"GetCapabilities: {capabilities}");
        Console.WriteLine($"notification id: {notifier.CurrentNotificationId}");

        await Task.Delay(TimeSpan.FromSeconds(4));
        await notifier.DismissAsync();

        Assert.AreEqual(0u, notifier.CurrentNotificationId, "dismiss cleared the id");
        Assert.IsTrue(notifier.SupportsActions, $"server reported: {capabilities}");
    }
}
