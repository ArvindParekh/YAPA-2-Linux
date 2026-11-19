using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>A day's summary as written, and the evidence fingerprint of each pomodoro it covered.</summary>
public sealed record StoredDay(DaySummary Summary, IReadOnlyDictionary<int, string> Fingerprints, DateTime WrittenUtc);

/// <summary>
/// Time that is off the record: forgotten afterwards, or gone off the record while it happened.
/// Nothing from any source is used for it.
/// </summary>
/// <param name="EndUtc">For time off the record until turned back on, far in the future.</param>
public sealed record OffRecordRange(long Id, DateTime StartUtc, DateTime EndUtc, string Kind)
{
    public const string Forgotten = "forgotten";
    public const string Live = "off the record";
}

/// <summary>
/// The journal's own tables in <c>Yapa.db</c>: the windows in front, the titles the user gave
/// pomodoros, the summaries Claude wrote (so a day opens at once and is only rewritten when its
/// evidence changes), and the time that is off the record. Older builds ignore these tables.
/// </summary>
public sealed class JournalStore : IDisposable
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS "ActivitySpans" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "StartUtc" TEXT NOT NULL, "EndUtc" TEXT NOT NULL, "App" TEXT NOT NULL, "Title" TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS "ActivitySpansByStart" ON "ActivitySpans" ("StartUtc");
        CREATE TABLE IF NOT EXISTS "JournalTitles" (
            "PomodoroId" INTEGER NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL, "EditedUtc" TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS "JournalDays" (
            "Day" TEXT NOT NULL PRIMARY KEY, "Summary" TEXT NOT NULL, "Fingerprints" TEXT NOT NULL, "WrittenUtc" TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS "OffRecord" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "StartUtc" TEXT NOT NULL, "EndUtc" TEXT NOT NULL, "Kind" TEXT NOT NULL, "CreatedUtc" TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS "OffRecordByEnd" ON "OffRecord" ("EndUtc");
        """;

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly object _gate = new();
    private readonly SqliteConnection? _connection;

    public JournalStore() : this(Path.Combine(EnsureData(), SqlitePomodoroRepository.DefaultFileName)) { }

    public JournalStore(string dbPath)
    {
        try
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            _connection.Open();
            Execute(CreateSql);
        }
        catch (SqliteException e)
        {
            // The timer works without it; the journal just has less to go on.
            Trace.WriteLine($"[journal] store unavailable: {e.Message}");
            _connection?.Dispose();
            _connection = null;
        }
    }

    private static string EnsureData()
    {
        AppPaths.EnsureCreated();
        return AppPaths.DataDirectory;
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The app of a span in which the journal was paused (Tor Browser was open): it says when,
    /// never what, and everything else found in that time is dropped.
    /// </summary>
    public const string Paused = "(paused)";

    /// <summary>
    /// No span is longer than this (the recorder starts a new one), so a day's spans are found
    /// by their start alone, through the index, however long the history grows.
    /// </summary>
    public static readonly TimeSpan LongestSpan = TimeSpan.FromHours(12);

    /// <returns>The span's id, to extend it; 0 when nothing could be written.</returns>
    public long BeginSpan(DateTime startUtc, string app, string title)
    {
        var id = Scalar("""
            INSERT INTO "ActivitySpans" ("StartUtc", "EndUtc", "App", "Title") VALUES ($start, $start, $app, $title);
            SELECT last_insert_rowid();
            """, ("$start", Format(startUtc)), ("$app", app), ("$title", title));
        return id is long l ? l : 0;
    }

    public void ExtendSpan(long id, DateTime endUtc)
    {
        if (id == 0) return;
        Execute("""UPDATE "ActivitySpans" SET "EndUtc" = $end WHERE "Id" = $id;""", ("$end", Format(endUtc)), ("$id", id));
    }

    public IReadOnlyList<WindowSpan> Spans(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<WindowSpan>();
        // Bounded on both sides of the start: "EndUtc >= $from" alone can't use the index,
        // and today's lookup would read every span ever recorded.
        Query("""
            SELECT "StartUtc", "EndUtc", "App", "Title" FROM "ActivitySpans"
            WHERE "StartUtc" >= $earliest AND "StartUtc" <= $to AND "EndUtc" >= $from ORDER BY "StartUtc";
            """, r =>
        {
            if (Parse(r.GetString(0)) is { } s && Parse(r.GetString(1)) is { } e) result.Add(new WindowSpan(s, e, r.GetString(2), r.GetString(3)));
        }, ("$earliest", Format(fromUtc - LongestSpan)), ("$from", Format(fromUtc)), ("$to", Format(toUtc)));
        return result;
    }

    /// <summary>
    /// Deletes what was recorded in [fromUtc, toUtc]: spans inside it go, spans across its
    /// edges are cut back to them (and one that covers it all is split in two).
    /// </summary>
    public void DeleteSpans(DateTime fromUtc, DateTime toUtc)
    {
        var from = Format(fromUtc);
        var to = Format(toUtc);
        var earliest = Format(fromUtc - LongestSpan);
        ExecuteAtomically("""
            INSERT INTO "ActivitySpans" ("StartUtc", "EndUtc", "App", "Title")
                SELECT $to, "EndUtc", "App", "Title" FROM "ActivitySpans"
                WHERE "StartUtc" >= $earliest AND "StartUtc" < $from AND "EndUtc" > $to;
            UPDATE "ActivitySpans" SET "EndUtc" = $from
                WHERE "StartUtc" >= $earliest AND "StartUtc" < $from AND "EndUtc" > $from;
            DELETE FROM "ActivitySpans" WHERE "StartUtc" >= $from AND "StartUtc" <= $to AND "EndUtc" <= $to;
            UPDATE "ActivitySpans" SET "StartUtc" = $to WHERE "StartUtc" >= $from AND "StartUtc" < $to;
            """, ("$earliest", earliest), ("$from", from), ("$to", to));
    }

    /// <summary>The hours (UTC, on the hour) in [fromUtc, toUtc) in which a window was recorded: for marking days at the computer.</summary>
    public IReadOnlyList<DateTime> RecordedHours(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<DateTime>();
        Query("""
            SELECT DISTINCT substr("StartUtc", 1, 13) FROM "ActivitySpans"
            WHERE "StartUtc" >= $from AND "StartUtc" < $to AND "App" <> $paused;
            """, r =>
        {
            if (DateTime.TryParseExact(r.GetString(0), "yyyy-MM-dd HH", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hour))
                result.Add(DateTime.SpecifyKind(hour, DateTimeKind.Utc));
        }, ("$from", Format(fromUtc)), ("$to", Format(toUtc)), ("$paused", Paused));
        return result;
    }

    public int SpanCount()
    {
        var n = Scalar("""SELECT COUNT(*) FROM "ActivitySpans";""");
        return n is long l ? (int)l : 0;
    }

    // ── Off the record ───────────────────────────────────────────────────────

    public long AddOffRecord(DateTime startUtc, DateTime endUtc, string kind)
    {
        var id = Scalar("""
            INSERT INTO "OffRecord" ("StartUtc", "EndUtc", "Kind", "CreatedUtc") VALUES ($start, $end, $kind, $at);
            SELECT last_insert_rowid();
            """, ("$start", Format(startUtc)), ("$end", Format(endUtc)), ("$kind", kind), ("$at", Format(DateTime.UtcNow)));
        return id is long l ? l : 0;
    }

    public void SetOffRecordEnd(long id, DateTime endUtc)
        => Execute("""UPDATE "OffRecord" SET "EndUtc" = $end WHERE "Id" = $id;""", ("$end", Format(endUtc)), ("$id", id));

    /// <summary>Time off the record that overlaps [fromUtc, toUtc].</summary>
    public IReadOnlyList<OffRecordRange> OffRecord(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<OffRecordRange>();
        // Few rows, and indexed by end: those ending before the range are skipped.
        Query("""
            SELECT "Id", "StartUtc", "EndUtc", "Kind" FROM "OffRecord" WHERE "EndUtc" >= $from AND "StartUtc" <= $to ORDER BY "StartUtc";
            """, r =>
        {
            if (Parse(r.GetString(1)) is { } s && Parse(r.GetString(2)) is { } e) result.Add(new OffRecordRange(r.GetInt64(0), s, e, r.GetString(3)));
        }, ("$from", Format(fromUtc)), ("$to", Format(toUtc)));
        return result;
    }

    // ── Titles the user gave ─────────────────────────────────────────────────

    public IReadOnlyDictionary<int, string> Titles(IEnumerable<int> pomodoroIds)
    {
        var ids = pomodoroIds.Distinct().ToList();
        var result = new Dictionary<int, string>();
        if (ids.Count == 0) return result;
        Query($"""SELECT "PomodoroId", "Title" FROM "JournalTitles" WHERE "PomodoroId" IN ({string.Join(",", ids)});""",
            r => result[r.GetInt32(0)] = r.GetString(1));
        return result;
    }

    /// <param name="title">Null or blank gives the pomodoro back to the summary.</param>
    public void SetTitle(int pomodoroId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            Execute("""DELETE FROM "JournalTitles" WHERE "PomodoroId" = $id;""", ("$id", pomodoroId));
        else
            Execute("""
                INSERT INTO "JournalTitles" ("PomodoroId", "Title", "EditedUtc") VALUES ($id, $title, $at)
                ON CONFLICT ("PomodoroId") DO UPDATE SET "Title" = excluded."Title", "EditedUtc" = excluded."EditedUtc";
                """, ("$id", pomodoroId), ("$title", title.Trim()), ("$at", Format(DateTime.UtcNow)));
    }

    // ── Summaries ────────────────────────────────────────────────────────────

    public void RemoveTitles(IEnumerable<int> pomodoroIds)
    {
        var ids = pomodoroIds.Distinct().ToList();
        if (ids.Count > 0) Execute($"""DELETE FROM "JournalTitles" WHERE "PomodoroId" IN ({string.Join(",", ids)});""");
    }

    public StoredDay? Day(DateOnly day)
    {
        StoredDay? result = null;
        Query("""SELECT "Summary", "Fingerprints", "WrittenUtc" FROM "JournalDays" WHERE "Day" = $day;""", r =>
        {
            try
            {
                var summary = ReadSummary(JsonNode.Parse(r.GetString(0)));
                var prints = JsonSerializer.Deserialize<Dictionary<int, string>>(r.GetString(1)) ?? [];
                if (summary != null) result = new StoredDay(summary, prints, Parse(r.GetString(2)) ?? DateTime.UtcNow);
            }
            catch (JsonException) { }
        }, ("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return result;
    }

    public void SaveDay(DateOnly day, DaySummary summary, IReadOnlyDictionary<int, string> fingerprints)
    {
        Execute("""
            INSERT INTO "JournalDays" ("Day", "Summary", "Fingerprints", "WrittenUtc") VALUES ($day, $summary, $prints, $at)
            ON CONFLICT ("Day") DO UPDATE SET "Summary" = excluded."Summary", "Fingerprints" = excluded."Fingerprints", "WrittenUtc" = excluded."WrittenUtc";
            """,
            ("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$summary", WriteSummary(summary).ToJsonString()),
            ("$prints", JsonSerializer.Serialize(fingerprints)),
            ("$at", Format(DateTime.UtcNow)));
    }

    /// <summary>
    /// Everything the journal recorded or wrote. The pomodoros themselves stay, and so does the
    /// time off the record: the browsers and Claude Code still have what happened in it, and
    /// only those ranges keep it out.
    /// </summary>
    public void Forget()
    {
        Execute("""DELETE FROM "ActivitySpans"; DELETE FROM "JournalDays"; DELETE FROM "JournalTitles";""");
    }

    private static JsonObject WriteSummary(DaySummary s) => new()
    {
        ["headline"] = s.Headline,
        ["summary"] = s.Summary,
        ["sessions"] = new JsonArray(s.Sessions.Select(x => (JsonNode)new JsonObject { ["number"] = x.Number, ["title"] = x.Title, ["summary"] = x.Summary }).ToArray()),
        ["pomodoros"] = new JsonArray(s.Pomodoros.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.PomodoroId, ["title"] = p.Title, ["detail"] = p.Detail,
            ["projects"] = new JsonArray(p.Projects.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        }).ToArray()),
    };

    // The stored shape is the answer's shape, so one reader does both.
    private static DaySummary? ReadSummary(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var answer = new JsonObject
        {
            ["day"] = new JsonObject { ["headline"] = o["headline"]?.DeepClone(), ["summary"] = o["summary"]?.DeepClone() },
            ["sessions"] = o["sessions"]?.DeepClone(),
            ["pomodoros"] = o["pomodoros"]?.DeepClone(),
        };
        return JournalPrompt.Parse(answer);
    }

    // ── SQL ──────────────────────────────────────────────────────────────────

    private static string Format(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTime? Parse(string s)
        => DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;

    private void Execute(string sql, params (string Name, object Value)[] parameters) => Run(sql, c => c.ExecuteNonQuery(), parameters);

    // All of it or none: a half-done delete could leave a span cut but its other half missing.
    private void ExecuteAtomically(string sql, params (string Name, object Value)[] parameters) => Run(sql, c =>
    {
        using var transaction = c.Connection!.BeginTransaction();
        c.Transaction = transaction;
        var rows = c.ExecuteNonQuery();
        transaction.Commit();
        return rows;
    }, parameters);

    private object? Scalar(string sql, params (string Name, object Value)[] parameters) => Run(sql, c => c.ExecuteScalar(), parameters);

    private void Query(string sql, Action<SqliteDataReader> row, params (string Name, object Value)[] parameters)
        => Run<object?>(sql, c =>
        {
            using var reader = c.ExecuteReader();
            while (reader.Read()) row(reader);
            return null;
        }, parameters);

    private T? Run<T>(string sql, Func<SqliteCommand, T> body, (string Name, object Value)[] parameters)
    {
        if (_connection == null) return default;
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return body(command);
            }
        }
        catch (SqliteException e)
        {
            Trace.WriteLine($"[journal] {e.Message}");
            return default;
        }
    }

    public void Dispose()
    {
        lock (_gate) _connection?.Dispose();
    }
}
