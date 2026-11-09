using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;

namespace YAPA.Avalonia.Persistence;

/// <summary>
/// Pomodoro history in SQLite (<c>Yapa.db</c> in <see cref="AppPaths.DataDirectory"/>).
///
/// The file format is owned by history, not by this class: databases were created by EF Core,
/// either <c>EnsureCreated</c> (earlier Avalonia builds) or <c>Migrate</c> (the Windows app,
/// whose migrations appended columns, so column order differs). Everything here therefore
/// addresses columns by name, reads timestamps tolerantly, and writes them exactly as EF did.
/// </summary>
public sealed class SqlitePomodoroRepository : IPomodoroRepository, IDisposable
{
    public const string DefaultFileName = "Yapa.db";

    /// <summary>
    /// What EF Core (all versions this app shipped with) writes for a <see cref="DateTime"/>:
    /// seven fraction digits with trailing zeros trimmed, and no '.' when the fraction is zero.
    /// Older builds parse it back with <c>DateTime.Parse</c>.
    /// </summary>
    internal const string StorageFormat = "yyyy'-'MM'-'dd HH':'mm':'ss.FFFFFFF";

    // Verbatim the DDL EF Core's EnsureCreated emits for PomodoroEntity, so a database we create
    // is indistinguishable from one an older build created.
    internal const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS "Pomodoros" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Pomodoros" PRIMARY KEY AUTOINCREMENT,
            "ProfileName" TEXT NULL,
            "DateTime" TEXT NOT NULL,
            "Count" INTEGER NOT NULL,
            "DurationMin" INTEGER NOT NULL
        );
        """;

    private const string SelectColumns = """SELECT "Id", "ProfileName", "DateTime", "Count", "DurationMin" FROM "Pomodoros" """;

    private static readonly string[] ParseFormats =
    [
        "yyyy'-'MM'-'dd HH':'mm':'ss.FFFFFFFK",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK",
        "yyyy'-'MM'-'dd HH':'mmK",
        "yyyy'-'MM'-'dd'T'HH':'mmK",
        "yyyy'-'MM'-'dd",
    ];

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<DateTime> _utcNow;
    private bool _disposed;

    /// <summary>Production constructor, the one the DI container picks.</summary>
    public SqlitePomodoroRepository(IEnvironment environment)
        : this(DefaultPath(), TimeZoneInfo.Local, () => DateTime.UtcNow)
    {
    }

    /// <param name="timeZone">Zone that defines "today" for <see cref="CompletedToday"/>.</param>
    /// <param name="utcNow">Clock for <see cref="CompletedToday"/>.</param>
    public SqlitePomodoroRepository(string dbPath, TimeZoneInfo timeZone, Func<DateTime> utcNow)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One long-lived connection guarded by a lock. Pooling would only keep the file
            // open after Dispose.
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            EnsureSchema();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    private static string DefaultPath()
    {
        AppPaths.EnsureCreated();
        return Path.Combine(AppPaths.DataDirectory, DefaultFileName);
    }

    public int CompletedToday()
    {
        var nowUtc = LocalCalendar.ToUtc(_utcNow());
        var today = LocalCalendar.DateOf(nowUtc, _timeZone);

        // A local day lies within now ± (24h + max UTC offset); ±2 days covers every zone.
        // The exact decision is made per record on its local date, which sidesteps
        // converting a (possibly nonexistent, DST) local midnight back to UTC.
        return Between(nowUtc.AddDays(-2), nowUtc.AddDays(2))
            .Where(p => LocalCalendar.DateOf(p.DateTime, _timeZone) == today)
            .Sum(p => p.Count);
    }

    public void Add(PomodoroEntity pomo)
    {
        ArgumentNullException.ThrowIfNull(pomo);

        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Pomodoros" ("ProfileName", "DateTime", "Count", "DurationMin")
                VALUES ($profile, $dateTime, $count, $duration);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$profile", (object?)pomo.ProfileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$dateTime", Format(pomo.DateTime));
            command.Parameters.AddWithValue("$count", pomo.Count);
            command.Parameters.AddWithValue("$duration", pomo.DurationMin);
            pomo.Id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public void Delete(int id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """DELETE FROM "Pomodoros" WHERE "Id" = $id;""";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    [Obsolete("Use Between(fromUtc, toUtcExclusive) or All().")]
    public IEnumerable<PomodoroEntity> After(DateTime date) => Between(date, DateTime.MaxValue);

    public IReadOnlyList<PomodoroEntity> Between(DateTime fromUtc, DateTime toUtcExclusive)
    {
        var from = LocalCalendar.ToUtc(fromUtc);
        var to = LocalCalendar.ToUtc(toUtcExclusive);
        if (from > to)
            throw new ArgumentOutOfRangeException(nameof(toUtcExclusive), "The range ends before it starts.");

        // SQL can only compare text, and stored text is not uniformly formatted: 'T' sorts after
        // ' ', and an offset suffix shifts the real instant by up to 14h. So prefilter on whole
        // dates with a day of slack either side (any such row's text date is within one day
        // of its real UTC date), then decide exactly on the parsed value.
        var lower = from.Date > DateTime.MinValue.Date ? FormatDate(from.Date.AddDays(-1)) : null;
        var upper = to.Date < DateTime.MaxValue.Date.AddDays(-1) ? FormatDate(to.Date.AddDays(2)) : null;

        return Query(
                """WHERE ($lower IS NULL OR "DateTime" >= $lower) AND ($upper IS NULL OR "DateTime" < $upper)""",
                ("$lower", lower),
                ("$upper", upper))
            .Where(p => p.DateTime >= from && p.DateTime < to)
            .ToList();
    }

    public IReadOnlyList<PomodoroEntity> All() => Query(string.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private List<PomodoroEntity> Query(string whereClause, params (string Name, string? Value)[] parameters)
    {
        var result = new List<PomodoroEntity>();
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = SelectColumns + whereClause;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // A row whose timestamp we cannot interpret is skipped rather than failing the
                // whole dashboard. It stays in the file untouched.
                if (reader.GetValue(2) is not string text || !TryParse(text, out var utc))
                    continue;

                result.Add(new PomodoroEntity
                {
                    Id = reader.GetInt32(0),
                    ProfileName = reader.IsDBNull(1) ? null! : reader.GetString(1),
                    DateTime = utc,
                    Count = reader.GetInt32(3),
                    DurationMin = reader.GetInt32(4),
                });
            }
        }

        // Ordered here, not in SQL: text order is wrong across mixed formats.
        result.Sort((a, b) => a.DateTime != b.DateTime ? a.DateTime.CompareTo(b.DateTime) : a.Id.CompareTo(b.Id));
        return result;
    }

    private void EnsureSchema()
    {
        // EF Core already creates its databases in WAL mode; this is a no-op for those.
        Execute("PRAGMA journal_mode = WAL;");
        Execute(CreateTableSql);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """PRAGMA table_info("Pomodoros");""";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        foreach (var required in new[] { "Id", "DateTime", "Count" })
        {
            if (!columns.Contains(required))
                throw new InvalidDataException($"Pomodoros table in {_connection.DataSource} has no \"{required}\" column.");
        }

        // A Windows database that stopped before the 2017 migrations lacks these. Add them the
        // way those migrations did, so the file stays readable by every build.
        if (!columns.Contains("DurationMin"))
            Execute("""ALTER TABLE "Pomodoros" ADD COLUMN "DurationMin" INTEGER NOT NULL DEFAULT 0;""");
        if (!columns.Contains("ProfileName"))
            Execute("""ALTER TABLE "Pomodoros" ADD COLUMN "ProfileName" TEXT NULL;""");
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static string Format(DateTime value)
        => LocalCalendar.ToUtc(value).ToString(StorageFormat, CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime value)
        => value.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Accepts ' ' or 'T', 0–7 fraction digits, and an optional 'Z' or numeric offset.
    /// A timestamp without an offset is UTC.
    /// </summary>
    internal static bool TryParse(string text, out DateTime utc)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, ParseFormats, CultureInfo.InvariantCulture, styles, out utc)
            || DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, styles, out utc))
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return true;
        }
        return false;
    }
}
