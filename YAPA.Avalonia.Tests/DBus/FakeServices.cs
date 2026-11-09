using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tmds.DBus.Protocol;

namespace YAPA.Avalonia.Tests.DBus;

/// <summary>
/// A connection that owns a well-known name on the private bus and can emit signals from it,
/// built on Tmds.DBus.Protocol's server-side API (name ownership + <see cref="IPathMethodHandler"/>).
/// </summary>
internal class FakeService : IDisposable
{
    protected DBusConnection Connection { get; }

    protected FakeService(DBusConnection connection) => Connection = connection;

    protected static async Task<DBusConnection> ConnectAndOwnAsync(string address, string name, Func<DBusConnection, IPathMethodHandler?> handler)
    {
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        if (handler(connection) is { } h)
            connection.AddMethodHandler(h);
        await connection.RequestNameAsync(name);
        return connection;
    }

    public static async Task<FakeService> OwnAsync(string address, string name) =>
        new(await ConnectAndOwnAsync(address, name, _ => null));

    public void EmitBool(string path, string iface, string member, bool value)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteSignalHeader(null, path, iface, member, "b");
        writer.WriteBool(value);
        Assert.IsTrue(Connection.TrySendMessage(writer.CreateMessage()), "signal not sent");
    }

    public void EmitPropertiesChanged(string path, string iface, string property, bool value)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteSignalHeader(null, path, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as");
        writer.WriteString(iface);
        var dict = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString(property);
        writer.WriteVariantBool(value);
        writer.WriteDictionaryEnd(dict);
        writer.WriteArray(Array.Empty<string>());
        Assert.IsTrue(Connection.TrySendMessage(writer.CreateMessage()), "signal not sent");
    }

    public virtual void Dispose() => Connection.Dispose();
}

internal sealed record NotifyCall(
    string AppName, uint ReplacesId, string AppIcon, string Summary, string Body,
    string[] Actions, Dictionary<string, VariantValue> Hints, int ExpireTimeout);

/// <summary>Minimal org.freedesktop.Notifications server that records what it is sent.</summary>
internal sealed class FakeNotificationServer : FakeService
{
    public const string Path = "/org/freedesktop/Notifications";
    public const string Interface = "org.freedesktop.Notifications";

    private readonly object _gate = new();
    private readonly List<NotifyCall> _notifyCalls = new();
    private readonly List<uint> _closeCalls = new();
    private readonly List<MethodContext> _parked = new();
    private readonly HashSet<uint> _live = new();
    private uint _nextId;

    private FakeNotificationServer(DBusConnection connection, string[] capabilities, uint firstId) : base(connection)
    {
        Capabilities = capabilities;
        _nextId = firstId;
    }

    public string[] Capabilities { get; }

    /// <summary>Accept Notify but never reply, like a wedged server.</summary>
    public bool HangNotify { get; set; }

    public IReadOnlyList<NotifyCall> NotifyCalls { get { lock (_gate) return _notifyCalls.ToArray(); } }

    public IReadOnlyList<uint> CloseCalls { get { lock (_gate) return _closeCalls.ToArray(); } }

    /// <param name="firstId">Lets a "restarted" server hand out ids that differ from the first one's.</param>
    public static async Task<FakeNotificationServer> StartAsync(string address, string[]? capabilities = null, uint firstId = 1)
    {
        FakeNotificationServer? server = null;
        var connection = await ConnectAndOwnAsync(address, "org.freedesktop.Notifications",
            _ => new Handler(() => server!));
        server = new FakeNotificationServer(connection, capabilities ?? ["body", "actions"], firstId);
        return server;
    }

    public void EmitActionInvoked(uint id, string key)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteSignalHeader(null, Path, Interface, "ActionInvoked", "us");
        writer.WriteUInt32(id);
        writer.WriteString(key);
        Assert.IsTrue(Connection.TrySendMessage(writer.CreateMessage()), "signal not sent");
    }

    public void EmitNotificationClosed(uint id, uint reason = 2)
    {
        lock (_gate)
            _live.Remove(id);
        using var writer = Connection.GetMessageWriter();
        writer.WriteSignalHeader(null, Path, Interface, "NotificationClosed", "uu");
        writer.WriteUInt32(id);
        writer.WriteUInt32(reason);
        Assert.IsTrue(Connection.TrySendMessage(writer.CreateMessage()), "signal not sent");
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            foreach (var context in _parked)
                context.Dispose();
            _parked.Clear();
        }
        base.Dispose();
    }

    private void Handle(MethodContext context)
    {
        var request = context.Request;
        if (request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return;
        }

        switch (request.MemberAsString)
        {
            case "GetCapabilities":
            {
                using var writer = context.CreateReplyWriter("as");
                writer.WriteArray(Capabilities);
                context.Reply(writer.CreateMessage());
                break;
            }
            case "Notify":
            {
                var reader = request.GetBodyReader();
                var call = new NotifyCall(
                    reader.ReadString(), reader.ReadUInt32(), reader.ReadString(), reader.ReadString(),
                    reader.ReadString(), reader.ReadArrayOfString(), reader.ReadDictionaryOfStringToVariantValue(),
                    reader.ReadInt32());
                uint id;
                lock (_gate)
                {
                    _notifyCalls.Add(call);
                    // Same as GNOME: replacing a live notification keeps its id; an unknown or
                    // zero replaces_id gets a fresh one.
                    id = _live.Contains(call.ReplacesId) ? call.ReplacesId : _nextId++;
                    _live.Add(id);
                    if (HangNotify)
                    {
                        context.DisposesAsynchronously = true;
                        _parked.Add(context);
                        return;
                    }
                }
                using var writer = context.CreateReplyWriter("u");
                writer.WriteUInt32(id);
                context.Reply(writer.CreateMessage());
                break;
            }
            case "CloseNotification":
            {
                var reader = request.GetBodyReader();
                var id = reader.ReadUInt32();
                lock (_gate)
                {
                    _closeCalls.Add(id);
                    _live.Remove(id);
                }
                using (var writer = context.CreateReplyWriter(null!))
                    context.Reply(writer.CreateMessage());
                EmitNotificationClosed(id, reason: 3);
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private sealed class Handler(Func<FakeNotificationServer> server) : IPathMethodHandler
    {
        public string Path => FakeNotificationServer.Path;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            server().Handle(context);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Fake logind: PrepareForSleep, session lookup and the session's LockedHint.</summary>
internal sealed class FakeLogin1 : FakeService
{
    public const string ManagerPath = "/org/freedesktop/login1";
    public const string SessionPath = "/org/freedesktop/login1/session/test";
    private readonly ConcurrentQueue<string> _requestedSessionIds = new();

    private FakeLogin1(DBusConnection connection) : base(connection) { }

    public IReadOnlyCollection<string> RequestedSessionIds => _requestedSessionIds.ToArray();

    /// <param name="supportsSessions">False: answer session lookups with an error, like a logind
    /// without a session for us.</param>
    public static async Task<FakeLogin1> StartAsync(string address, bool supportsSessions = true)
    {
        FakeLogin1? login1 = null;
        var connection = await ConnectAndOwnAsync(address, "org.freedesktop.login1",
            _ => new Handler(() => login1!, supportsSessions));
        login1 = new FakeLogin1(connection);
        return login1;
    }

    public void EmitPrepareForSleep(bool sleeping) =>
        EmitBool(ManagerPath, "org.freedesktop.login1.Manager", "PrepareForSleep", sleeping);

    public void EmitLockedHint(bool locked) =>
        EmitPropertiesChanged(SessionPath, "org.freedesktop.login1.Session", "LockedHint", locked);

    private sealed class Handler(Func<FakeLogin1> login1, bool supportsSessions) : IPathMethodHandler
    {
        public string Path => ManagerPath;
        public bool HandlesChildPaths => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (!supportsSessions)
            {
                context.ReplyError("org.freedesktop.login1.NoSessionForPID", "no session");
                return ValueTask.CompletedTask;
            }

            if (request.PathAsString == ManagerPath && request.MemberAsString == "GetSession")
            {
                var reader = request.GetBodyReader();
                login1()._requestedSessionIds.Enqueue(reader.ReadString());
                using var writer = context.CreateReplyWriter("o");
                writer.WriteObjectPath(SessionPath);
                context.Reply(writer.CreateMessage());
            }
            else if (request.PathAsString == ManagerPath + "/session/auto"
                     && request.InterfaceAsString == "org.freedesktop.DBus.Properties"
                     && request.MemberAsString == "Get")
            {
                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariantString("auto-resolved");
                context.Reply(writer.CreateMessage());
            }
            else
            {
                context.ReplyUnknownMethodError();
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Collects events raised on background threads so a test can await them in order.</summary>
internal sealed class EventRecorder<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly SemaphoreSlim _available = new(0);

    public void Record(T item)
    {
        _items.Enqueue(item);
        _available.Release();
    }

    public async Task<T> NextAsync(int timeoutMs = 5000)
    {
        if (!await _available.WaitAsync(timeoutMs))
            Assert.Fail($"no event within {timeoutMs} ms");
        Assert.IsTrue(_items.TryDequeue(out var item));
        return item!;
    }

    public int Count => _items.Count;
}

internal static class Poll
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(50);
        }
    }
}
