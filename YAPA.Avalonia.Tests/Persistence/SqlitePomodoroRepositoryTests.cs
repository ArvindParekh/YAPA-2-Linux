using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Persistence;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.Persistence;

[TestClass]
public class SqlitePomodoroRepositoryTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>The DDL EF Core's EnsureCreated produced, as read back from a real user database.</summary>
    private const string EfEnsureCreatedDdl = """
        CREATE TABLE "Pomodoros" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Pomodoros" PRIMARY KEY AUTOINCREMENT,
            "ProfileName" TEXT NULL,
            "DateTime" TEXT NOT NULL,
            "Count" INTEGER NOT NULL,
            "DurationMin" INTEGER NOT NULL
        )
        """;

    private TempDatabase _db = null!;
    private DateTime _now;

    [TestInitialize]
    public void Setup()
    {
        _db = new TempDatabase();
        _now = Utc("2026-11-11T15:00:00Z"); // 10:00 EST
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    private SqlitePomodoroRepository Open(TimeZoneInfo? zone = null)
        => new(_db.FilePath, zone ?? NewYork, () => _now);

    private static DateTime Utc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static PomodoroEntity Entity(string isoUtc, int count = 1, int minutes = 25, string? profile = "Default")
        => new() { DateTime = Utc(isoUtc), Count = count, DurationMin = minutes, ProfileName = profile! };

    private string PomodorosDdl() => (string)_db.Scalar("SELECT sql FROM sqlite_master WHERE name = 'Pomodoros'")!;

    // ---- schema ---------------------------------------------------------------------------

    [TestMethod]
    public void NewDatabase_GetsExactlyTheEfSchema_InWalMode()
    {
        using (Open()) { }

        Assert.AreEqual(EfEnsureCreatedDdl, PomodorosDdl());
        Assert.AreEqual("wal", _db.Scalar("PRAGMA journal_mode"));
    }

    [TestMethod]
    public void ExistingEnsureCreatedDatabase_IsOpenedAsIs()
    {
        _db.Execute(EfEnsureCreatedDdl);
        _db.InsertRaw("2026-05-09 18:43:07.4066513", profile: "Default");
        _db.InsertRaw("2026-05-09 18:43:20.170725", profile: "Default");
        _db.Execute("""DELETE FROM "Pomodoros" WHERE "Id" = 2;"""); // AUTOINCREMENT must not reuse 2

        using (var repo = Open())
        {
            var first = repo.All().Single();
            Assert.AreEqual(1, first.Id);
            Assert.AreEqual(Utc("2026-05-09T18:43:07.4066513Z"), first.DateTime);
            Assert.AreEqual(DateTimeKind.Utc, first.DateTime.Kind);

            var added = Entity("2026-05-10T09:00:00Z");
            repo.Add(added);
            Assert.AreEqual(3, added.Id);
        }

        Assert.AreEqual(EfEnsureCreatedDdl, PomodorosDdl());
    }

    [TestMethod]
    public void ExistingWindowsMigratedDatabase_ColumnOrderAndHistoryTableAreTolerated()
    {
        // What EF Core 1.1 Migrate() left behind in the Windows app: the two later columns were
        // appended by ALTER TABLE, so they come after DateTime.
        _db.Execute("""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            CREATE TABLE "Pomodoros" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Pomodoros" PRIMARY KEY AUTOINCREMENT,
                "Count" INTEGER NOT NULL,
                "DateTime" TEXT NOT NULL
            );
            ALTER TABLE "Pomodoros" ADD COLUMN "DurationMin" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE "Pomodoros" ADD COLUMN "ProfileName" TEXT;
            INSERT INTO "__EFMigrationsHistory" VALUES
                ('20170204162935_Initial', '1.1.0-rtm-22752'),
                ('20171011164113_Duration min', '1.1.0-rtm-22752'),
                ('20171016181624_profile name', '1.1.0-rtm-22752');
            INSERT INTO "Pomodoros" ("Count", "DateTime", "DurationMin", "ProfileName")
                VALUES (1, '2017-10-20 08:15:00.123', 25, 'Work'), (2, '2017-02-05 10:00:00', 0, NULL);
            """);
        var ddlBefore = PomodorosDdl();

        using (var repo = Open())
        {
            var rows = repo.All();
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(Utc("2017-02-05T10:00:00Z"), rows[0].DateTime); // ordered by time, not Id
            Assert.AreEqual(2, rows[0].Count);
            Assert.IsNull(rows[0].ProfileName);
            Assert.AreEqual("Work", rows[1].ProfileName);
            Assert.AreEqual(25, rows[1].DurationMin);

            repo.Add(Entity("2026-01-01T00:00:00Z", minutes: 50, profile: "Work"));
            Assert.AreEqual(3, repo.All().Count);
        }

        Assert.AreEqual(ddlBefore, PomodorosDdl());
        Assert.AreEqual(3L, _db.Scalar("""SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
    }

    [TestMethod]
    public void DatabaseFromBeforeThe2017Migrations_GetsTheMissingColumns()
    {
        _db.Execute("""
            CREATE TABLE "Pomodoros" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Pomodoros" PRIMARY KEY AUTOINCREMENT,
                "Count" INTEGER NOT NULL,
                "DateTime" TEXT NOT NULL
            );
            INSERT INTO "Pomodoros" ("Count", "DateTime") VALUES (1, '2017-02-05 10:00:00');
            """);

        using var repo = Open();

        var row = repo.All().Single();
        Assert.AreEqual(0, row.DurationMin);
        Assert.IsNull(row.ProfileName);
        repo.Add(Entity("2026-01-01T00:00:00Z"));
        Assert.AreEqual(2, repo.All().Count);
    }

    [TestMethod]
    public void TableWithoutCoreColumns_IsRejectedClearly()
    {
        _db.Execute("""CREATE TABLE "Pomodoros" ("Id" INTEGER PRIMARY KEY, "When" TEXT);""");

        Assert.ThrowsException<InvalidDataException>(() => Open());
    }

    // ---- writes ---------------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_PreservesEveryField()
    {
        using var repo = Open();
        var a = Entity("2026-11-11T14:25:31.1234567Z", count: 1, minutes: 25, profile: "Work");
        var b = Entity("2026-11-11T14:55:00Z", count: 2, minutes: 50, profile: null);

        repo.Add(a);
        repo.Add(b);

        Assert.AreEqual(1, a.Id);
        Assert.AreEqual(2, b.Id);
        var rows = repo.All();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual((1, "Work", a.DateTime, 1, 25), (rows[0].Id, rows[0].ProfileName, rows[0].DateTime, rows[0].Count, rows[0].DurationMin));
        Assert.AreEqual((2, (string?)null, b.DateTime, 2, 50), (rows[1].Id, (string?)rows[1].ProfileName, rows[1].DateTime, rows[1].Count, rows[1].DurationMin));
    }

    [DataTestMethod]
    [DataRow("2026-05-09T18:43:07.4066513Z", "2026-05-09 18:43:07.4066513")]
    [DataRow("2026-05-09T18:43:07.4066510Z", "2026-05-09 18:43:07.406651")]
    [DataRow("2026-05-09T18:43:07.5Z", "2026-05-09 18:43:07.5")]
    [DataRow("2026-05-09T18:43:07Z", "2026-05-09 18:43:07")]
    public void Add_WritesTheTextFormatEfCoreWrote(string isoUtc, string expectedText)
    {
        using (var repo = Open())
            repo.Add(Entity(isoUtc));

        Assert.AreEqual(expectedText, _db.Strings("""SELECT "DateTime" FROM "Pomodoros" """).Single());
    }

    [TestMethod]
    public void Add_StoresLocalKindAsUtc()
    {
        var utc = Utc("2026-11-11T14:00:00Z");
        using (var repo = Open())
            repo.Add(new PomodoroEntity { DateTime = utc.ToLocalTime(), Count = 1, DurationMin = 25 });

        Assert.AreEqual("2026-11-11 14:00:00", _db.Strings("""SELECT "DateTime" FROM "Pomodoros" """).Single());
    }

    [TestMethod]
    public void Delete_RemovesOnlyThatRow()
    {
        using var repo = Open();
        var a = Entity("2026-11-11T10:00:00Z");
        var b = Entity("2026-11-11T11:00:00Z");
        repo.Add(a);
        repo.Add(b);

        repo.Delete(a.Id);
        repo.Delete(12345);

        Assert.AreEqual(b.Id, repo.All().Single().Id);
    }

    // ---- reading legacy text --------------------------------------------------------------

    [DataTestMethod]
    [DataRow("2026-05-09 18:43:07.4066513", "2026-05-09T18:43:07.4066513Z")]
    [DataRow("2026-05-09 18:43:07.40665", "2026-05-09T18:43:07.40665Z")]
    [DataRow("2026-05-09 18:43:07", "2026-05-09T18:43:07Z")]
    [DataRow("2026-05-09T18:43:07.4066513", "2026-05-09T18:43:07.4066513Z")]
    [DataRow("2026-05-09T18:43:07", "2026-05-09T18:43:07Z")]
    [DataRow("2026-05-09T18:43:07Z", "2026-05-09T18:43:07Z")]
    [DataRow("2026-05-09 18:43:07.123Z", "2026-05-09T18:43:07.123Z")]
    [DataRow("2026-05-09T13:43:07-05:00", "2026-05-09T18:43:07Z")]
    [DataRow("2026-05-10T00:13:07.5+05:30", "2026-05-09T18:43:07.5Z")]
    [DataRow("2026-05-09 18:43", "2026-05-09T18:43:00Z")]
    [DataRow(" 2026-05-09 18:43:07 ", "2026-05-09T18:43:07Z")]
    public void LegacyTimestamps_AreReadAsUtc(string storedText, string expectedUtc)
    {
        _db.Execute(EfEnsureCreatedDdl);
        _db.InsertRaw(storedText);

        using var repo = Open();
        var row = repo.All().Single();

        Assert.AreEqual(Utc(expectedUtc), row.DateTime);
        Assert.AreEqual(DateTimeKind.Utc, row.DateTime.Kind);
    }

    [TestMethod]
    public void UnreadableTimestamp_IsSkipped_NotFatal()
    {
        _db.Execute(EfEnsureCreatedDdl);
        _db.InsertRaw("not a date");
        _db.InsertRaw("2026-05-09 18:43:07");

        using var repo = Open();

        Assert.AreEqual(1, repo.All().Count);
        Assert.AreEqual(2L, _db.Scalar("""SELECT COUNT(*) FROM "Pomodoros" """)); // left in place
    }

    // ---- CompletedToday -------------------------------------------------------------------

    [TestMethod]
    public void CompletedToday_UsesTheLocalCalendarDay()
    {
        _db.Execute(EfEnsureCreatedDdl);
        _db.InsertRaw("2026-11-11 04:59:59.9999999", count: 10); // Nov 10, 23:59:59 EST
        _db.InsertRaw("2026-11-11 05:00:00", count: 1);           // Nov 11, 00:00 EST
        _db.InsertRaw("2026-11-11T23:30:00-05:00", count: 2);     // Nov 11, 23:30 EST, stored with an offset
        _db.InsertRaw("2026-11-12T04:59:59.9999999", count: 4);   // Nov 11, 23:59:59 EST
        _db.InsertRaw("2026-11-12 05:00:00", count: 20);          // Nov 12, 00:00 EST

        using var repo = Open();

        Assert.AreEqual(7, repo.CompletedToday());
    }

    [TestMethod]
    public void CompletedToday_FollowsTheClock()
    {
        using var repo = Open();
        repo.Add(Entity("2026-11-11T14:00:00Z"));

        Assert.AreEqual(1, repo.CompletedToday());
        _now = Utc("2026-11-12T05:00:00Z"); // local midnight passes
        Assert.AreEqual(0, repo.CompletedToday());
    }

    [TestMethod]
    public void CompletedToday_OnTheTwentyFiveHourFallBackDay()
    {
        _now = Utc("2026-11-01T17:00:00Z");
        using var repo = Open();
        foreach (var t in new[] { "2026-11-01T03:59:59Z", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z", "2026-11-01T06:30:00Z", "2026-11-02T04:59:59Z", "2026-11-02T05:00:00Z" })
            repo.Add(Entity(t));

        Assert.AreEqual(4, repo.CompletedToday());
    }

    [TestMethod]
    public void CompletedToday_OnTheTwentyThreeHourSpringForwardDay()
    {
        _now = Utc("2026-03-08T17:00:00Z");
        using var repo = Open();
        foreach (var t in new[] { "2026-03-08T04:59:59Z", "2026-03-08T05:00:00Z", "2026-03-08T07:00:00Z", "2026-03-09T03:59:59Z", "2026-03-09T04:00:00Z" })
            repo.Add(Entity(t));

        Assert.AreEqual(3, repo.CompletedToday());
    }

    [DataTestMethod]
    [DataRow("Pacific/Kiritimati", "2026-11-11T10:00:00Z", "2026-11-12T10:00:00Z")] // UTC+14
    [DataRow("Etc/GMT+12", "2026-11-11T12:00:00Z", "2026-11-12T12:00:00Z")]         // UTC-12
    public void CompletedToday_InExtremeOffsets(string zoneId, string dayStartUtc, string nextDayStartUtc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var start = Utc(dayStartUtc);
        var next = Utc(nextDayStartUtc);
        _now = start.AddHours(1);
        using var repo = Open(zone);
        repo.Add(new PomodoroEntity { DateTime = start.AddTicks(-1), Count = 100 });
        repo.Add(new PomodoroEntity { DateTime = start, Count = 1 });
        repo.Add(new PomodoroEntity { DateTime = next.AddTicks(-1), Count = 2 });
        repo.Add(new PomodoroEntity { DateTime = next, Count = 100 });

        Assert.AreEqual(3, repo.CompletedToday());
    }

    // ---- Between --------------------------------------------------------------------------

    [TestMethod]
    public void Between_IsHalfOpen_AndOrdered()
    {
        var from = Utc("2026-11-11T10:00:00Z");
        var to = Utc("2026-11-11T12:00:00Z");
        using var repo = Open();
        foreach (var t in new[] { to.AddTicks(-1), from.AddTicks(-1), to, from, from.AddHours(1) })
            repo.Add(new PomodoroEntity { DateTime = t, Count = 1 });

        var rows = repo.Between(from, to);

        CollectionAssert.AreEqual(new[] { from, from.AddHours(1), to.AddTicks(-1) }, rows.Select(r => r.DateTime).ToArray());
        Assert.AreEqual(0, repo.Between(from, from).Count);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => repo.Between(to, from));
    }

    [TestMethod]
    public void Between_FindsRowsWhoseTextDateDiffersFromTheirUtcDate()
    {
        _db.Execute(EfEnsureCreatedDdl);
        _db.InsertRaw("2026-11-10T23:30:00-05:00"); // 2026-11-11 04:30Z, text says the 10th
        _db.InsertRaw("2026-11-12T01:00:00+14:00"); // 2026-11-11 11:00Z, text says the 12th
        _db.InsertRaw("2026-11-11T12:00:00");       // 'T' sorts after ' ' in text comparison
        _db.InsertRaw("2026-11-11 13:00:00");

        using var repo = Open();

        Assert.AreEqual(1, repo.Between(Utc("2026-11-11T04:00:00Z"), Utc("2026-11-11T05:00:00Z")).Count);
        Assert.AreEqual(1, repo.Between(Utc("2026-11-11T10:30:00Z"), Utc("2026-11-11T11:30:00Z")).Count);
        Assert.AreEqual(1, repo.Between(Utc("2026-11-11T11:30:00Z"), Utc("2026-11-11T12:30:00Z")).Count);
        Assert.AreEqual(4, repo.Between(Utc("2026-11-11T00:00:00Z"), Utc("2026-11-12T00:00:00Z")).Count);
        Assert.AreEqual(0, repo.Between(Utc("2026-11-12T00:00:00Z"), Utc("2026-11-13T00:00:00Z")).Count);
    }

    [TestMethod]
    public void Between_AcceptsTheFullDateRange_AndLocalKind()
    {
        using var repo = Open();
        repo.Add(Entity("0001-01-01T00:00:00Z"));
        repo.Add(Entity("2026-11-11T14:00:00Z"));
        repo.Add(Entity("9999-12-31T23:59:59Z"));

        Assert.AreEqual(3, repo.Between(DateTime.MinValue, DateTime.MaxValue).Count);

        var localFrom = Utc("2026-11-11T13:00:00Z").ToLocalTime();
        var localTo = Utc("2026-11-11T15:00:00Z").ToLocalTime();
        Assert.AreEqual(1, repo.Between(localFrom, localTo).Count);
    }

#pragma warning disable CS0618 // After is obsolete but still part of the contract.
    [TestMethod]
    public void After_IsInclusive()
    {
        using var repo = Open();
        repo.Add(Entity("2026-11-11T10:00:00Z"));
        repo.Add(Entity("2026-11-11T11:00:00Z"));

        Assert.AreEqual(1, repo.After(Utc("2026-11-11T11:00:00Z")).Count());
    }
#pragma warning restore CS0618

    // ---- threading and lifetime -----------------------------------------------------------

    [TestMethod]
    public void ConcurrentAdds_AllLand_WithUniqueIds()
    {
        const int threads = 8, perThread = 50;
        using var repo = Open();
        var ids = new ConcurrentBag<int>();
        var errors = new ConcurrentQueue<Exception>();
        using var start = new Barrier(threads + 1);

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var i = 0; i < perThread; i++)
                {
                    var p = new PomodoroEntity { DateTime = _now.AddSeconds(-(t * perThread + i)), Count = 1, DurationMin = 25, ProfileName = $"p{t}" };
                    repo.Add(p);
                    ids.Add(p.Id);
                    if (i % 10 == 0)
                        _ = repo.CompletedToday(); // readers interleaved with writers
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
        })).ToList();

        workers.ForEach(w => w.Start());
        start.SignalAndWait();
        workers.ForEach(w => w.Join());

        Assert.AreEqual(0, errors.Count, errors.FirstOrDefault()?.ToString());
        Assert.AreEqual(threads * perThread, ids.Distinct().Count());
        Assert.AreEqual(threads * perThread, repo.All().Count);
        Assert.AreEqual(threads * perThread, repo.CompletedToday());
    }

    [TestMethod]
    public void Dispose_ClosesTheFile_AndIsIdempotent()
    {
        var repo = Open();
        repo.Add(Entity("2026-11-11T14:00:00Z"));

        repo.Dispose();
        repo.Dispose();

        // The last connection closing checkpoints and removes the WAL.
        Assert.IsFalse(File.Exists(_db.FilePath + "-wal"));
        Assert.ThrowsException<ObjectDisposedException>(() => repo.All());
        Assert.ThrowsException<ObjectDisposedException>(() => repo.Add(Entity("2026-11-11T14:00:00Z")));

        using var reopened = Open();
        Assert.AreEqual(1, reopened.All().Count);
    }
}
