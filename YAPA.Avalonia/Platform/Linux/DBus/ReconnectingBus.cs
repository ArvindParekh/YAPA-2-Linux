using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace YAPA.Avalonia.Platform.Linux.DBus;

/// <summary>
/// One bus connection that is opened on first use and re-opened after it drops.
///
/// A Tmds <see cref="DBusConnection"/> is single-use: once disconnected it stays dead and its
/// match rules die with it. So "reconnect" means building a new connection and re-running
/// <c>onConnected</c>, which is where owners install their signal subscriptions.
/// </summary>
internal sealed class ReconnectingBus : IDisposable
{
    private readonly string _name;
    private readonly string? _address;
    private readonly Func<DBusConnection, Task> _onConnected;
    private readonly DBusPolicy _policy;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();

    private DBusConnection? _current;
    private Task<DBusConnection?>? _connecting;
    private bool _reportedNoAddress;

    /// <param name="name">Used in log lines only ("session", "system").</param>
    /// <param name="onConnected">
    /// Runs on every new connection before it is handed out. Exceptions are logged, not fatal:
    /// a failed subscription should not make the connection unusable for method calls.
    /// </param>
    public ReconnectingBus(string name, string? address, Func<DBusConnection, Task> onConnected, DBusPolicy policy)
    {
        _name = name;
        _address = string.IsNullOrEmpty(address) ? null : address;
        _onConnected = onConnected;
        _policy = policy;
    }

    /// <summary>The live connection, connecting first if needed. Null when the bus is unavailable.</summary>
    public Task<DBusConnection?> GetAsync()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested)
                return Task.FromResult<DBusConnection?>(null);
            if (_current is not null)
                return Task.FromResult<DBusConnection?>(_current);
            // Single flight: concurrent callers share one attempt. A finished attempt that
            // returned null is not cached, so the next caller tries again.
            if (_connecting is { IsCompleted: false } pending)
                return pending;
            return _connecting = ConnectAsync();
        }
    }

    private async Task<DBusConnection?> ConnectAsync()
    {
        if (_address is null)
        {
            if (!_reportedNoAddress)
                DBusLog.Write($"{_name} bus: no address, staying inert");
            _reportedNoAddress = true;
            return null;
        }

        var connection = new DBusConnection(_address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(_policy.CallTimeout, _disposed.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            connection.Dispose();
            if (!_disposed.IsCancellationRequested)
                DBusLog.Write($"{_name} bus: connect to {_address} failed: {ex.Message}");
            return null;
        }

        try
        {
            await _onConnected(connection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DBusLog.Write($"{_name} bus: setup after connect failed: {ex.Message}");
        }

        lock (_gate)
        {
            if (_disposed.IsCancellationRequested)
            {
                connection.Dispose();
                return null;
            }
            // Published only after onConnected, so nobody calls methods before signal
            // subscriptions and capability queries are in place.
            _current = connection;
        }
        DBusLog.Write($"{_name} bus: connected as {connection.UniqueName}");
        _ = WatchForDisconnectAsync(connection);
        return connection;
    }

    private async Task WatchForDisconnectAsync(DBusConnection connection)
    {
        Exception? reason = await connection.DisconnectedAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_current, connection))
                _current = null;
            if (_disposed.IsCancellationRequested)
                return;
        }
        connection.Dispose();
        DBusLog.Write($"{_name} bus: disconnected ({reason?.Message ?? "no reason"}); reconnecting");

        // Reconnect proactively rather than on next use: a notification still on screen can only
        // deliver its button click if our signal subscription exists again.
        var delay = _policy.InitialReconnectDelay;
        while (true)
        {
            try
            {
                await Task.Delay(delay, _disposed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (await GetAsync().ConfigureAwait(false) is not null)
                return;
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _policy.MaxReconnectDelay.Ticks));
        }
    }

    public void Dispose()
    {
        DBusConnection? current;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested)
                return;
            _disposed.Cancel();
            current = _current;
            _current = null;
        }
        current?.Dispose();
    }
}
