using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Persistence;

/// <param name="ElapsedSeconds">
/// How far the interval had got: at a stop or reset, before it was thrown away; otherwise
/// the timer's elapsed time just after the event.
/// </param>
public sealed record TimerEventRecord(long Id, DateTime AtUtc, string Kind, string From, string To, int Pomodoro, int Cycle,
    bool LongBreak, bool Automatic, int LengthSeconds, int ElapsedSeconds);

/// <summary>
/// Every timer transition, in its own table of <c>Yapa.db</c>. The history only keeps
/// completed pomodoros; this is what answers the rest: how long the breaks really were,
/// how many pomodoros were abandoned, when the day actually started. Older builds ignore
/// the table.
/// </summary>
public sealed class TimerEventLog : IDisposable
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS "TimerEvents" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "AtUtc" TEXT NOT NULL, "Kind" TEXT NOT NULL, "FromPhase" TEXT NOT NULL, "ToPhase" TEXT NOT NULL,
            "Pomodoro" INTEGER NOT NULL, "Cycle" INTEGER NOT NULL, "LongBreak" INTEGER NOT NULL, "Automatic" INTEGER NOT NULL,
            "LengthSeconds" INTEGER NOT NULL, "ElapsedSeconds" INTEGER NOT NULL);
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection? _connection;
    private readonly IPomodoroEngine _engine;
    private readonly IClock _clock;

    // Where the interval was at the last event, to know how far it had got when it is dropped.
    private int _lastElapsed;
    private TimeSpan _lastAt;
    private bool _wasRunning;

    public TimerEventLog(IPomodoroEngine engine, IClock clock) : this(engine, clock, DefaultPath()) { }

    public TimerEventLog(IPomodoroEngine engine, IClock clock, string dbPath)
    {
        _engine = engine;
        _clock = clock;
        try
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = CreateSql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            // The timer works without it; only the extra questions go unanswered.
            Trace.WriteLine($"[events] log unavailable: {e.Message}");
            _connection?.Dispose();
            _connection = null;
        }
        _engine.Transitioned += OnTransition;
    }

    private static string DefaultPath()
    {
        AppPaths.EnsureCreated();
        return Path.Combine(AppPaths.DataDirectory, SqlitePomodoroRepository.DefaultFileName);
    }

    private void OnTransition(PomodoroTransition t)
    {
        var now = _clock.Monotonic;
        var elapsed = t.Kind is PomodoroTransitionKind.Stopped or PomodoroTransitionKind.Reset or PomodoroTransitionKind.MarkedDone
                or PomodoroTransitionKind.Moved
            ? _lastElapsed + (_wasRunning ? (int)(now - _lastAt).TotalSeconds : 0)
            : _engine.Elapsed;
        _lastElapsed = _engine.Elapsed;
        _lastAt = now;
        _wasRunning = _engine.IsRunning;

        if (_connection == null) return;
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO "TimerEvents" ("AtUtc", "Kind", "FromPhase", "ToPhase", "Pomodoro", "Cycle", "LongBreak", "Automatic", "LengthSeconds", "ElapsedSeconds")
                    VALUES ($at, $kind, $from, $to, $pomodoro, $cycle, $long, $auto, $length, $elapsed);
                    """;
                command.Parameters.AddWithValue("$at", _clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$kind", t.Kind.ToString());
                command.Parameters.AddWithValue("$from", t.From.ToString());
                command.Parameters.AddWithValue("$to", t.To.ToString());
                command.Parameters.AddWithValue("$pomodoro", _engine.Index);
                command.Parameters.AddWithValue("$cycle", _engine.Cycle);
                command.Parameters.AddWithValue("$long", t.IsLongBreak ? 1 : 0);
                command.Parameters.AddWithValue("$auto", t.Automatic ? 1 : 0);
                command.Parameters.AddWithValue("$length", _engine.CurrentIntervalLength);
                command.Parameters.AddWithValue("$elapsed", Math.Max(0, elapsed));
                command.ExecuteNonQuery();
            }
        }
        catch (Exception e) when (e is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            Trace.WriteLine($"[events] can't record {t.Kind}: {e.Message}");
        }
    }

    public IReadOnlyList<TimerEventRecord> All()
    {
        var result = new List<TimerEventRecord>();
        if (_connection == null) return result;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "AtUtc", "Kind", "FromPhase", "ToPhase", "Pomodoro", "Cycle", "LongBreak", "Automatic", "LengthSeconds", "ElapsedSeconds"
                FROM "TimerEvents" ORDER BY "Id";
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTime.TryParseExact(reader.GetString(1), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                    continue;
                result.Add(new TimerEventRecord(reader.GetInt64(0), DateTime.SpecifyKind(at, DateTimeKind.Utc), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7) != 0,
                    reader.GetInt32(8) != 0, reader.GetInt32(9), reader.GetInt32(10)));
            }
        }
        return result;
    }

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        lock (_gate) _connection?.Dispose();
    }
}
