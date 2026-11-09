using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace YAPA.Avalonia.Platform.Linux.DBus;

/// <summary>
/// <see cref="IDesktopNotifier"/> over <c>org.freedesktop.Notifications</c>
/// (Desktop Notifications Specification 1.2).
/// </summary>
public sealed class DBusNotifier : IDesktopNotifier
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";
    private const string AppName = "YAPA 2";
    private const string DesktopEntry = "yapa2";
    private const byte UrgencyNormal = 1;

    private readonly DBusPolicy _policy;
    private readonly ReconnectingBus _bus;
    // Serialises Notify/Close: each Notify must see the id the previous one returned, or two
    // quick calls would both pass replaces_id=0 and stack two notifications.
    private readonly SemaphoreSlim _serial = new(1, 1);
    private volatile bool _disposed;
    // Id of the notification currently on screen; 0 = none. Read by signal handlers on the
    // Tmds dispatch thread, so always accessed through Volatile/Interlocked.
    private uint _currentId;
    // Null = unknown, queried before the next Notify. Reset when the server changes owner.
    private volatile string[]? _capabilities;

    /// <param name="sessionBusAddress">D-Bus address of the session bus. Tests pass a private bus.</param>
    public DBusNotifier(string? sessionBusAddress, DBusPolicy? policy = null)
    {
        _policy = policy ?? DBusPolicy.Default;
        _bus = new ReconnectingBus("session", sessionBusAddress, OnConnectedAsync, _policy);
    }

    public bool SupportsActions => Array.IndexOf(_capabilities ?? [], "actions") >= 0;

    /// <summary>What the server reported from <c>GetCapabilities</c>; empty until connected.</summary>
    public IReadOnlyList<string> Capabilities => _capabilities ?? [];

    /// <summary>Server id of the notification on screen; 0 when none. For diagnostics and tests.</summary>
    public uint CurrentNotificationId => Volatile.Read(ref _currentId);

    public event Action<string>? ActionInvoked;

    public Task ShowAsync(DesktopNotification notification)
    {
        if (notification is null)
        {
            DBusLog.Write("Notify skipped: notification is null");
            return Task.CompletedTask;
        }
        return RunSerialAsync("Notify", connection => NotifyAsync(connection, notification));
    }

    public Task DismissAsync() => RunSerialAsync("CloseNotification", CloseAsync);

    public void Dispose()
    {
        // Deliberately leaves the notification on screen: Dispose is synchronous and a
        // fire-and-forget close could be dropped with the socket. Await DismissAsync first
        // if it should go.
        _disposed = true;
        _bus.Dispose();
    }

    private Task RunSerialAsync(string operation, Func<DBusConnection, Task> action)
    {
        if (_disposed)
            return Task.CompletedTask;

        // Task.Run so that no part of this (not even opening the socket) runs on the caller's
        // UI thread, and so continuations never try to marshal back to it.
        var work = Task.Run(async () =>
        {
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                var connection = await _bus.GetAsync().ConfigureAwait(false);
                if (connection is null || _disposed)
                    return;
                await action(connection).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DBusLog.Write($"{operation} failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _serial.Release();
            }
        });
        return BoundedAsync(work, operation);
    }

    private async Task BoundedAsync(Task work, string operation)
    {
        try
        {
            await work.WaitAsync(_policy.CallTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            DBusLog.Write($"{operation} still pending after {_policy.CallTimeout.TotalSeconds:0.#}s; finishing in background");
        }
    }

    private async Task NotifyAsync(DBusConnection connection, DesktopNotification notification)
    {
        if (_capabilities is null)
            await QueryCapabilitiesAsync(connection).ConfigureAwait(false);

        // Body is plain text in our API, but servers advertising body-markup (GNOME does) parse
        // it, so a task name containing '&' or '<' would be mangled or dropped.
        if (Array.IndexOf(_capabilities ?? [], "body-markup") >= 0)
            notification = notification with { Body = EscapeMarkup(notification.Body) };

        var replacesId = Volatile.Read(ref _currentId);
        var id = await connection.CallMethodAsync(
                BuildNotify(connection, notification, replacesId),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return reader.ReadUInt32();
                })
            .WaitAsync(_policy.CallTimeout)
            .ConfigureAwait(false);
        Volatile.Write(ref _currentId, id);
    }

    private async Task CloseAsync(DBusConnection connection)
    {
        var id = Volatile.Read(ref _currentId);
        if (id == 0)
            return;
        await connection.CallMethodAsync(BuildClose(connection, id)).WaitAsync(_policy.CallTimeout).ConfigureAwait(false);
        // The server also emits NotificationClosed, but clear now so a Show that follows
        // immediately doesn't pass a dead replaces_id.
        Interlocked.CompareExchange(ref _currentId, 0, id);
    }

    private static MessageBuffer BuildNotify(DBusConnection connection, DesktopNotification n, uint replacesId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "Notify", "susssasa{sv}i");
        writer.WriteString(AppName);
        writer.WriteUInt32(replacesId);
        writer.WriteString(n.IconName ?? "");
        writer.WriteString(n.Title ?? "");
        writer.WriteString(n.Body ?? "");

        var actions = writer.WriteArrayStart(DBusType.String);
        foreach (var action in n.Actions ?? [])
        {
            writer.WriteString(action.Key ?? "");
            writer.WriteString(action.Label ?? "");
        }
        writer.WriteArrayEnd(actions);

        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("urgency");
        // Normal for both kinds: GNOME hides low-urgency banners entirely, and critical would
        // break through Do Not Disturb, which a timer has no business doing.
        writer.WriteVariantByte(UrgencyNormal);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString(DesktopEntry);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("category");
        writer.WriteVariantString(n.Category ?? "");
        // Persistence is "transient=false", not "resident=true". Resident means the server keeps
        // the notification after an action is clicked; GNOME honours that, leaving a stale
        // [Start break] in the message list that would fire again if clicked.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("transient");
        writer.WriteVariantBool(!n.Persistent);
        writer.WriteDictionaryEnd(hints);

        // 0 = never expire, -1 = server default. GNOME ignores this, other servers don't.
        writer.WriteInt32(n.Persistent ? 0 : -1);
        return writer.CreateMessage();
    }

    private static string EscapeMarkup(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static MessageBuffer BuildClose(DBusConnection connection, uint id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "CloseNotification", "u");
        writer.WriteUInt32(id);
        return writer.CreateMessage();
    }

    // MessageWriter is a ref struct, so messages are built in synchronous helpers like this one
    // and only the finished MessageBuffer crosses an await.
    private static MessageBuffer BuildGetCapabilities(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "GetCapabilities");
        return writer.CreateMessage();
    }

    private async Task OnConnectedAsync(DBusConnection connection)
    {
        // The returned subscription handles are not kept: they die with the connection, and
        // the connection is what ReconnectingBus disposes and replaces.
        //
        // Sender is the well-known name, not its current unique owner: the bus resolves it per
        // message, so the match keeps working when the server restarts (Alt+F2 r on X11).
        await connection.WatchSignalAsync(Service, ObjectPath, Interface, "ActionInvoked",
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Id: reader.ReadUInt32(), Key: reader.ReadString());
            },
            (exception, signal) =>
            {
                if (exception is null)
                    OnActionInvoked(signal.Id, signal.Key);
            },
            readerState: null, emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);

        await connection.WatchSignalAsync(Service, ObjectPath, Interface, "NotificationClosed",
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return reader.ReadUInt32();
            },
            (exception, id) =>
            {
                if (exception is null && id != 0)
                    Interlocked.CompareExchange(ref _currentId, 0, id);
            },
            readerState: null, emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);

        var ownerChanged = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = "org.freedesktop.DBus",
            Interface = "org.freedesktop.DBus",
            Member = "NameOwnerChanged",
            Arg0 = Service,
        };
        await connection.AddMatchAsync(ownerChanged,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadString();
                reader.ReadString();
                return reader.ReadString();
            },
            (exception, newOwner, _, _) =>
            {
                if (exception is not null)
                    return;
                // A different server (or a restarted one) may have different capabilities, and
                // our id meant nothing to it. Re-query lazily on the next Show.
                DBusLog.Write($"{Service} owner changed to '{newOwner}'");
                _capabilities = null;
                Volatile.Write(ref _currentId, 0);
            },
            ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);

        await QueryCapabilitiesAsync(connection).ConfigureAwait(false);
    }

    private async Task QueryCapabilitiesAsync(DBusConnection connection)
    {
        try
        {
            var capabilities = await connection.CallMethodAsync(BuildGetCapabilities(connection),
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return reader.ReadArrayOfString();
                    })
                .WaitAsync(_policy.CallTimeout)
                .ConfigureAwait(false);
            _capabilities = capabilities;
            DBusLog.Write($"notification server capabilities: {string.Join(", ", capabilities)}");
        }
        catch (Exception ex)
        {
            // Typically ServiceUnknown: YAPA autostarted before gnome-shell claimed the name.
            // Left null so the next Show asks again.
            DBusLog.Write($"GetCapabilities failed: {ex.Message}");
        }
    }

    private void OnActionInvoked(uint id, string key)
    {
        // Other apps' notifications emit ActionInvoked on the same interface; only ours count.
        if (id == 0 || id != Volatile.Read(ref _currentId))
            return;
        try
        {
            ActionInvoked?.Invoke(key);
        }
        catch (Exception ex)
        {
            DBusLog.Write($"ActionInvoked handler threw: {ex}");
        }
    }
}
