using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// Read-only SQL over the pomodoro history, for questions no fixed statistic answers
/// ("my best weekday this month", "how much on the Deep profile since Monday").
///
/// The query never touches the real database. Each call loads the history into a private
/// in-memory database, already converted to local time: the stored timestamps are UTC
/// text in several historical formats, which a model would get wrong (time zones, DST,
/// the 'T' variants), and the real file must never be written by a generated query.
/// </summary>
public static class HistoryQuery
{
    public const int MaxRows = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>For the model: the table it queries.</summary>
    public const string Schema = """
        Table pomodoros, one row per recorded pomodoro, all times in the user's local time zone:
          id           INTEGER  the record's id (for edit_history)
          started_at   TEXT     'YYYY-MM-DD HH:MM:SS', when it began (ended_at minus its minutes)
          ended_at     TEXT     'YYYY-MM-DD HH:MM:SS', when it was completed (this is what is recorded)
          date         TEXT     'YYYY-MM-DD', the local date it ended on (the day it counts for)
          hour         INTEGER  0-23, the local hour it started in
          weekday      TEXT     'Monday' … 'Sunday', of date
          weekday_num  INTEGER  1 = Monday … 7 = Sunday
          iso_week     TEXT     'YYYY-Www', ISO week of date (weeks start on Monday)
          minutes      INTEGER  its length in minutes (0 in very old records)
          count        INTEGER  pomodoros the row stands for (almost always 1; sum this to count)
          profile      TEXT     the profile it was done in (may be NULL in old records)
        """;

    public static JsonObject Run(string sql, IReadOnlyList<PomodoroEntity> history, TimeZoneInfo zone)
    {
        var text = sql.Trim().TrimEnd(';').Trim();
        if (text.Length == 0) throw new ToolException("The query is empty.");

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Load(connection, history, zone);

        // After loading, nothing may change: a query like "DELETE FROM pomodoros" fails here
        // instead of quietly emptying the copy and reporting success.
        Execute(connection, "PRAGMA query_only = ON;");

        using var command = connection.CreateCommand();
        command.CommandText = text;
        // A runaway query (a recursive CTE without an end) is interrupted rather than left
        // spinning. SqliteCommand.Cancel doesn't stop a statement that is already stepping;
        // sqlite3_interrupt does, and is safe to call from another thread.
        var handle = connection.Handle;
        using var watchdog = new System.Threading.Timer(_ => SQLitePCL.raw.sqlite3_interrupt(handle), null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        try
        {
            using var reader = command.ExecuteReader();
            var columns = new JsonArray(Enumerable.Range(0, reader.FieldCount).Select(i => (JsonNode)reader.GetName(i)).ToArray());
            var rows = new JsonArray();
            var truncated = false;
            while (reader.Read())
            {
                if (rows.Count == MaxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new JsonArray();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(reader.GetValue(i) switch
                    {
                        DBNull => null,
                        long l => JsonValue.Create(l),
                        double d => JsonValue.Create(Math.Round(d, 4)),
                        string s => JsonValue.Create(s),
                        var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                    });
                }
                rows.Add(row);
            }

            var result = new JsonObject { ["columns"] = columns, ["rows"] = rows };
            if (truncated) result["truncated"] = $"Only the first {MaxRows} rows; aggregate in SQL instead.";
            return result;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 9) // SQLITE_INTERRUPT
        {
            throw new ToolException($"The query took longer than {Timeout.TotalSeconds:0} seconds and was stopped.");
        }
        catch (SqliteException e)
        {
            throw new ToolException($"SQL error: {e.Message}");
        }
    }

    private static void Load(SqliteConnection connection, IReadOnlyList<PomodoroEntity> history, TimeZoneInfo zone)
    {
        Execute(connection, """
            CREATE TABLE pomodoros (
                id INTEGER PRIMARY KEY, started_at TEXT, ended_at TEXT, date TEXT, hour INTEGER,
                weekday TEXT, weekday_num INTEGER, iso_week TEXT, minutes INTEGER, count INTEGER, profile TEXT);
            CREATE INDEX pomodoros_date ON pomodoros(date);
            """);

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO pomodoros VALUES ($id, $started, $ended, $date, $hour, $weekday, $weekdayNum, $week, $minutes, $count, $profile);
            """;
        var parameters = new[] { "$id", "$started", "$ended", "$date", "$hour", "$weekday", "$weekdayNum", "$week", "$minutes", "$count", "$profile" }
            .Select(name => insert.Parameters.Add(name, SqliteType.Text))
            .ToArray();

        foreach (var p in history)
        {
            var endUtc = DateTime.SpecifyKind(p.DateTime, DateTimeKind.Utc);
            var ended = TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone);
            // Converted from UTC, not by subtracting local times, so a pomodoro across a DST change starts when it did.
            var started = TimeZoneInfo.ConvertTimeFromUtc(endUtc.AddMinutes(-Math.Max(0, p.DurationMin)), zone);
            var weekdayNum = ended.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)ended.DayOfWeek;

            parameters[0].Value = p.Id;
            parameters[1].Value = started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            parameters[2].Value = ended.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            parameters[3].Value = ended.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            parameters[4].Value = started.Hour;
            parameters[5].Value = ended.DayOfWeek.ToString();
            parameters[6].Value = weekdayNum;
            parameters[7].Value = $"{ISOWeek.GetYear(ended)}-W{ISOWeek.GetWeekOfYear(ended):00}";
            parameters[8].Value = p.DurationMin;
            parameters[9].Value = p.Count;
            parameters[10].Value = (object?)p.ProfileName ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
