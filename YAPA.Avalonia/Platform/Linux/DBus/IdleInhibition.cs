using System;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace YAPA.Avalonia.Platform.Linux.DBus;

/// <summary>
/// Whether the user is still at the computer while not touching it: a playing video (or a
/// call, or a presentation) holds an idle inhibitor with the session manager, which is what
/// keeps the screen from blanking. A locked screen is away whatever holds one. Answers false
/// when the session manager isn't GNOME's or the bus is missing.
/// </summary>
public sealed class IdleInhibition : IDisposable
{
    // GsmInhibitorFlag: "inhibit the session being marked as idle".
    private const uint IdleFlag = 8;

    private readonly ReconnectingBus? _bus;
    private readonly DBusPolicy _policy = DBusPolicy.Default;

    public IdleInhibition(string? sessionBusAddress = null)
    {
        var address = sessionBusAddress ?? (OperatingSystem.IsLinux() ? DBusAddress.Session : null);
        if (!string.IsNullOrEmpty(address)) _bus = new ReconnectingBus("session", address, _ => Task.CompletedTask, _policy);
    }

    /// <summary>Something keeps the session from going idle, and the screen isn't locked.</summary>
    public async Task<bool> IsWatchingAsync()
    {
        if (_bus == null || await _bus.GetAsync().ConfigureAwait(false) is not { } connection) return false;
        try
        {
            if (await CallBoolAsync(connection, "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "GetActive", null)
                    .ConfigureAwait(false))
                return false;
        }
        catch (DBusErrorReplyException)
        {
            // No GNOME shield to ask: the inhibitor alone decides.
        }
        try
        {
            return await CallBoolAsync(connection, "org.gnome.SessionManager", "/org/gnome/SessionManager", "org.gnome.SessionManager", "IsInhibited", IdleFlag)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusErrorReplyException or TimeoutException or DBusConnectionClosedException)
        {
            return false;
        }
    }

    private Task<bool> CallBoolAsync(DBusConnection connection, string service, string path, string iface, string member, uint? argument)
        => connection.CallMethodAsync(Build(connection, service, path, iface, member, argument), static (m, _) => m.GetBodyReader().ReadBool())
            .WaitAsync(_policy.CallTimeout);

    private static MessageBuffer Build(DBusConnection connection, string service, string path, string iface, string member, uint? argument)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(service, path, iface, member, argument is null ? null : "u");
        if (argument is { } a) writer.WriteUInt32(a);
        return writer.CreateMessage();
    }

    public void Dispose() => _bus?.Dispose();
}
