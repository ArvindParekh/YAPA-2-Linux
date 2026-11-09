using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace YAPA.Avalonia.Platform.Linux.DBus;

/// <summary>
/// Lock and sleep events from D-Bus.
///
/// Lock state comes from every source that exists, deduplicated on state:
/// <list type="bullet">
/// <item><c>org.gnome.ScreenSaver.ActiveChanged</c> (session bus): gnome-shell's shield.</item>
/// <item><c>org.freedesktop.ScreenSaver.ActiveChanged</c> (session bus): KDE and others. GNOME
/// also owns this name (a gnome-settings-daemon proxy), and KDE emits it on two paths, which is
/// why sources are merged by state instead of picking one.</item>
/// <item>logind <c>Session.LockedHint</c> (system bus): the lock state the shell reports to logind.
/// Used instead of logind's <c>Lock</c>/<c>Unlock</c> signals, which are requests to the locker
/// (e.g. <c>loginctl lock-session</c>), not confirmation that anything locked.</item>
/// </list>
/// Sleep comes from logind <c>Manager.PrepareForSleep</c> on the system bus.
/// </summary>
public sealed class DBusSessionMonitor : ISessionMonitor
{
    private const string Login1Service = "org.freedesktop.login1";
    private const string Login1Path = "/org/freedesktop/login1";
    private const string Login1Manager = "org.freedesktop.login1.Manager";
    private const string Login1Session = "org.freedesktop.login1.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly ReconnectingBus _sessionBus;
    private readonly ReconnectingBus _systemBus;
    private readonly DBusPolicy _policy;
    private readonly object _lockGate = new();
    private bool? _locked;
    private int _started;

    /// <param name="sessionBusAddress">Session bus (ScreenSaver sources); null disables them.</param>
    /// <param name="systemBusAddress">System bus (logind sources); null disables them.</param>
    public DBusSessionMonitor(string? sessionBusAddress, string? systemBusAddress, DBusPolicy? policy = null)
    {
        _policy = policy ?? DBusPolicy.Default;
        _sessionBus = new ReconnectingBus("session", sessionBusAddress, SubscribeSessionBusAsync, _policy);
        _systemBus = new ReconnectingBus("system", systemBusAddress, SubscribeSystemBusAsync, _policy);
    }

    public event Action<bool>? ScreenLockedChanged;

    public event Action<bool>? SleepingChanged;

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        // The buses connect independently: a missing system bus doesn't delay or block the
        // session one. GetAsync never throws; the catch is for anything unforeseen.
        return Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(_sessionBus.GetAsync(), _systemBus.GetAsync()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DBusLog.Write($"session monitor start failed: {ex}");
            }
        });
    }

    public void Dispose()
    {
        _sessionBus.Dispose();
        _systemBus.Dispose();
    }

    private async Task SubscribeSessionBusAsync(DBusConnection connection)
    {
        await TrySubscribeAsync("org.gnome.ScreenSaver.ActiveChanged", () => connection.WatchSignalAsync(
            "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged",
            ReadBool, (exception, active) => OnLockSource(exception, active),
            readerState: null, emitOnCapturedContext: false, ObserverFlags.None).AsTask()).ConfigureAwait(false);

        // Any path: KDE emits on both /ScreenSaver and /org/freedesktop/ScreenSaver.
        await TrySubscribeAsync("org.freedesktop.ScreenSaver.ActiveChanged", () => connection.WatchSignalAsync(
            "org.freedesktop.ScreenSaver", null!, "org.freedesktop.ScreenSaver", "ActiveChanged",
            ReadBool, (exception, active) => OnLockSource(exception, active),
            readerState: null, emitOnCapturedContext: false, ObserverFlags.None).AsTask()).ConfigureAwait(false);
    }

    private async Task SubscribeSystemBusAsync(DBusConnection connection)
    {
        await TrySubscribeAsync("login1.Manager.PrepareForSleep", () => connection.WatchSignalAsync(
            Login1Service, Login1Path, Login1Manager, "PrepareForSleep",
            ReadBool, (exception, sleeping) =>
            {
                if (exception is null)
                    Raise(SleepingChanged, sleeping, nameof(SleepingChanged));
            },
            readerState: null, emitOnCapturedContext: false, ObserverFlags.None).AsTask()).ConfigureAwait(false);

        await TrySubscribeAsync("login1.Session.LockedHint", async () =>
        {
            var sessionPath = await ResolveSessionPathAsync(connection).ConfigureAwait(false);
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = Login1Service,
                Path = sessionPath,
                Interface = PropertiesInterface,
                Member = "PropertiesChanged",
                Arg0 = Login1Session,
            };
            await connection.AddMatchAsync(rule, ReadLockedHint,
                (exception, lockedHint, _, _) =>
                {
                    if (lockedHint is { } locked)
                        OnLockSource(exception, locked);
                },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);
            DBusLog.Write($"watching LockedHint on {sessionPath}");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The object path of the login session we run in. Signals are emitted on the real path, so
    /// logind's "auto" alias only works for the lookup, not for the match rule.
    /// </summary>
    private async Task<string> ResolveSessionPathAsync(DBusConnection connection)
    {
        // XDG_SESSION_ID is set when launched from a session shell, but not for apps GNOME starts
        // under the systemd user manager (dock, autostart). There, "auto" resolves to the user's
        // display session.
        var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = await connection.CallMethodAsync(
                    BuildPropertyGet(connection, Login1Path + "/session/auto", Login1Session, "Id"),
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return Unwrap(reader.ReadVariantValue()).GetString();
                    })
                .WaitAsync(_policy.CallTimeout)
                .ConfigureAwait(false);
        }

        return await connection.CallMethodAsync(
                BuildGetSession(connection, sessionId),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return reader.ReadObjectPathAsString();
                })
            .WaitAsync(_policy.CallTimeout)
            .ConfigureAwait(false);
    }

    private static MessageBuffer BuildPropertyGet(DBusConnection connection, string path, string iface, string property)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Login1Service, path, PropertiesInterface, "Get", "ss");
        writer.WriteString(iface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetSession(DBusConnection connection, string sessionId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Login1Service, Login1Path, Login1Manager, "GetSession", "s");
        writer.WriteString(sessionId);
        return writer.CreateMessage();
    }

    private static bool ReadBool(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return reader.ReadBool();
    }

    /// <summary>LockedHint from a PropertiesChanged body, or null if this change didn't touch it.</summary>
    private static bool? ReadLockedHint(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        reader.ReadString();
        Dictionary<string, VariantValue> changed = reader.ReadDictionaryOfStringToVariantValue();
        return changed.TryGetValue("LockedHint", out var value) ? Unwrap(value).GetBool() : null;
    }

    private static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
            value = value.GetVariantValue();
        return value;
    }

    /// <summary>Each source is isolated: one failing to subscribe never stops the next.</summary>
    private static async Task TrySubscribeAsync(string source, Func<Task> subscribe)
    {
        try
        {
            await subscribe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DBusLog.Write($"{source}: unavailable ({ex.Message})");
        }
    }

    private void OnLockSource(Exception? exception, bool locked)
    {
        if (exception is not null)
            return;
        // Raised inside the lock so two sources reporting on different bus threads can't deliver
        // their transitions out of order. Handlers are expected to post to the UI, not block.
        lock (_lockGate)
        {
            if (_locked == locked)
                return;
            _locked = locked;
            Raise(ScreenLockedChanged, locked, nameof(ScreenLockedChanged));
        }
    }

    private static void Raise(Action<bool>? handler, bool value, string name)
    {
        try
        {
            handler?.Invoke(value);
        }
        catch (Exception ex)
        {
            DBusLog.Write($"{name} handler threw: {ex}");
        }
    }
}
