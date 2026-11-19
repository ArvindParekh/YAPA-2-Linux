using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Journal;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Tests.AppLogic;
using YAPA.Avalonia.Tests.Persistence;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.Journal;

/// <summary>The journal over a real history and event log, written up by a stand-in for claude.</summary>
[TestClass]
public class JournalServiceTests
{
    private TempDatabase _db = null!;
    private SqlitePomodoroRepository _history = null!;
    private JournalStore _store = null!;
    private JournalService _journal = null!;
    private string? _savedOverride;
    private string _calls = "";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Local;

    // A fixed day, in the local zone, so the bounds are what they'd be for the user.
    private static readonly DateOnly Day = new(2026, 11, 18);
    private static DateTime Local(int hour, int minute) => TimeZoneInfo.ConvertTimeToUtc(Day.ToDateTime(new TimeOnly(hour, minute)), Zone);

    [TestInitialize]
    public void Setup()
    {
        _db = new TempDatabase();
        _history = new SqlitePomodoroRepository(_db.FilePath, Zone, () => Local(20, 0));
        var events = new TimerEventLog(Substitute.For<IPomodoroEngine>(), Substitute.For<IClock>(), _db.FilePath);
        // Nothing of the machine's own: only what the test records.
        var settings = new JournalSettings(new JsonYapaSettings(new MemoryEnvironment(), new NewtonsoftJson()))
        {
            ClaudeCode = false, Git = false, Browsers = false,
        };
        _store = new JournalStore(_db.FilePath);
        _journal = new JournalService(_history, events, null, _store, settings, new ClaudeAccount(_db.Folder), zone: Zone, utcNow: () => Local(20, 0));
        _savedOverride = Environment.GetEnvironmentVariable(ClaudeCli.OverrideVariable);
        _calls = Path.Combine(_db.Folder, "calls.log");
        FakeClaude(signedOut: false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _journal.Dispose();
        _store.Dispose();
        Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, _savedOverride);
    }

    /// <summary>Writes "Wrote N" for every pomodoro it's asked about; logs what it was sent.</summary>
    private void FakeClaude(bool signedOut)
    {
        var path = Path.Combine(_db.Folder, "claude");
        var answer = signedOut
            ? """print(json.dumps({"type": "result", "is_error": True, "result": "Failed to authenticate: OAuth session expired and could not be refreshed"}))"""
            : """
              ids = [int(m) for m in re.findall(r"^## (?:Pomodoro|Off the timer) (-?\d+) .*?(?<!\(kept\))$", prompt, re.M)]
              print(json.dumps({"type": "result", "is_error": False, "structured_output": {
                  "pomodoros": [{"id": i, "title": f"Wrote {i}", "detail": "", "projects": ["YAPA"]} for i in ids],
                  "sessions": [{"number": 1, "title": "Morning", "summary": "Things got done."}],
                  "day": {"headline": "A day", "summary": "It went."}}}))
              """;
        File.WriteAllText(path, $"""
            #!/usr/bin/python3 -I
            import json, re, sys
            prompt = sys.stdin.read()
            with open({System.Text.Json.JsonSerializer.Serialize(_calls)}, "a") as f: f.write(prompt + "\n=====\n")
            {answer}
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, path);
    }

    private int Calls => File.Exists(_calls) ? File.ReadAllText(_calls).Split("=====").Length - 1 : 0;

    private void Event(DateTime at, string kind, int cycle)
        => _db.Execute("""
            INSERT INTO "TimerEvents" ("AtUtc", "Kind", "FromPhase", "ToPhase", "Pomodoro", "Cycle", "LongBreak", "Automatic", "LengthSeconds", "ElapsedSeconds")
            VALUES ($at, $kind, 'x', 'y', 1, $cycle, 0, 0, 3000, 0);
            """, ("$at", at.ToString("yyyy-MM-dd HH:mm:ss")), ("$kind", kind), ("$cycle", cycle));

    private void Window(int fromHour, int fromMinute, int toHour, int toMinute, string app, string title)
        => _store.ExtendSpan(_store.BeginSpan(Local(fromHour, fromMinute), app, title), Local(toHour, toMinute));

    private PomodoroEntity Pomodoro(DateTime end)
    {
        var p = new PomodoroEntity { DateTime = end, Count = 1, DurationMin = 50, ProfileName = "Pomodoro" };
        _history.Add(p);
        return p;
    }

    [TestMethod]
    public void A_day_has_its_pomodoros_with_their_real_start_their_sessions_and_their_evidence()
    {
        // Started 9:00, paused ten minutes, ended 10:00: it ran 9:00–10:00, not 9:10–10:00.
        Event(Local(9, 0), "WorkStarted", 1);
        Event(Local(10, 0), "WorkCompleted", 1);
        var first = Pomodoro(Local(10, 0));
        Event(Local(11, 0), "WorkStarted", 2);
        Event(Local(11, 50), "WorkCompleted", 2);
        Pomodoro(Local(11, 50));
        _store.ExtendSpan(_store.BeginSpan(Local(9, 5), "VS Code", "JournalService.cs — YAPA-2"), Local(9, 35));

        var day = _journal.Load(Day);

        Assert.AreEqual(2, day.Count);
        Assert.AreEqual(Local(9, 0), day.Entries[0].Block.StartUtc);
        Assert.AreEqual(2, day.Sessions.Count, "two cycles, two sessions");
        Assert.AreEqual("VS Code", day.Entries[0].Evidence.Apps.Single().App);
        Assert.AreEqual(first.Id, day.Entries[0].Block.PomodoroId);
        Assert.IsTrue(day.Stale, "nothing is written up yet");
        Assert.AreEqual("VS Code: JournalService.cs — YAPA-2", day.Entries[0].Title, "until then, the evidence names it");
    }

    [TestMethod]
    public async Task A_day_is_written_up_once_and_after_that_only_whats_new()
    {
        var first = Pomodoro(Local(10, 0));
        _store.ExtendSpan(_store.BeginSpan(Local(9, 15), "Zen", "Canvas"), Local(9, 40));

        var (day, problem) = await _journal.WriteAsync(Day);
        Assert.IsNull(problem);
        Assert.AreEqual($"Wrote {first.Id}", day.Entries.Single().Title);
        Assert.AreEqual("A day", day.Summary!.Headline);
        Assert.IsFalse(day.Stale);

        await _journal.WriteAsync(Day);
        Assert.AreEqual(1, Calls, "a day that's current isn't sent again");

        var second = Pomodoro(Local(11, 0));
        (day, _) = await _journal.WriteAsync(Day);
        Assert.AreEqual(2, Calls);
        var request = File.ReadAllText(_calls).Split("=====")[1];
        StringAssert.Contains(request, $"## Pomodoro {first.Id} ", "the first comes along as context");
        StringAssert.Contains(request, "(kept)\nTitle: Wrote " + first.Id);
        Assert.AreEqual($"Wrote {second.Id}", day.Entries[1].Title);
        Assert.AreEqual($"Wrote {first.Id}", day.Entries[0].Title, "and keeps its line");
    }

    [TestMethod]
    public async Task The_users_own_name_for_a_pomodoro_wins_and_the_day_is_rewritten_around_it()
    {
        var p = Pomodoro(Local(10, 0));
        await _journal.WriteAsync(Day);

        _journal.SetTitle(Day, p.Id, "Lecture recording");
        var day = _journal.Load(Day);
        Assert.AreEqual("Lecture recording", day.Entries.Single().Title);
        Assert.IsTrue(day.Stale);

        await _journal.WriteAsync(Day);
        StringAssert.Contains(File.ReadAllText(_calls).Split("=====")[1], "Title from the user: \"Lecture recording\"");
    }

    [TestMethod]
    public async Task Signed_out_it_says_so_and_keeps_the_evidence()
    {
        FakeClaude(signedOut: true);
        Pomodoro(Local(10, 0));

        var (day, problem) = await _journal.WriteAsync(Day);

        Assert.IsTrue(problem!.SignedOut);
        Assert.AreEqual(1, day.Count, "the day is still there, just not written up");
        Assert.IsTrue(day.Stale);
    }

    [TestMethod]
    public async Task The_whole_day_is_there_breaks_in_their_session_and_the_time_between_on_its_own()
    {
        Event(Local(9, 0), "WorkStarted", 1);
        Event(Local(10, 0), "WorkCompleted", 1);
        Pomodoro(Local(10, 0));
        Event(Local(10, 10), "WorkStarted", 1);
        Event(Local(11, 0), "WorkCompleted", 1);
        Pomodoro(Local(11, 0));
        Window(10, 2, 10, 6, "Zen", "Mafia, part 2 - YouTube — Zen Browser");
        Window(13, 0, 13, 30, "Zen", "Hacker News — Zen Browser");
        Window(19, 50, 19, 58, "Terminal", "~/YAPA-2");

        var day = _journal.Load(Day);

        var off = day.Entries.Where(e => e.OffTimer).ToList();
        Assert.AreEqual(2, day.Count, "pomodoros are counted as before");
        CollectionAssert.AreEqual(new[] { 1, 0, 0 }, off.Select(e => e.Session).ToList(), "the break is in its session; the rest stands on its own");
        Assert.AreEqual("Zen: Mafia, part 2 - YouTube", off[0].Title);
        Assert.IsTrue(off[2].InProgress, "still going: written once it's over");
        Assert.AreEqual(Local(10, 2), day.Entries[1].Block.StartUtc, "in order with the pomodoros");

        await _journal.WriteAsync(Day);
        var request = File.ReadAllText(_calls);
        StringAssert.Contains(request, "a break in session 1");
        StringAssert.Contains(request, "outside the sessions");
        Assert.IsFalse(request.Contains("~/YAPA-2"), "what's still going isn't sent yet");
        Assert.AreEqual($"Wrote {off[0].Block.PomodoroId}", _journal.Load(Day).Entries.First(e => e.OffTimer).Title);
    }

    [TestMethod]
    public void While_the_journal_is_paused_nothing_from_that_time_is_used_and_nothing_bridges_it()
    {
        Pomodoro(Local(10, 0));
        Window(9, 15, 9, 50, "Zen", "Canvas — Zen Browser");
        _store.ExtendSpan(_store.BeginSpan(Local(9, 30), JournalStore.Paused, ""), Local(9, 40));
        Window(14, 0, 14, 10, "Zen", "Before");
        _store.ExtendSpan(_store.BeginSpan(Local(14, 10), JournalStore.Paused, ""), Local(14, 20));
        Window(14, 20, 14, 30, "Zen", "After");

        var day = _journal.Load(Day);

        Assert.AreEqual(25, day.Entries[0].Evidence.Apps.Single().Minutes, "the ten paused minutes are cut out of the window across them");
        var off = day.Entries.Where(e => e.OffTimer).ToList();
        Assert.AreEqual(2, off.Count, "the paused time splits what's around it");
        Assert.IsTrue(off.All(e => e.Block.EndUtc <= Local(14, 10) || e.Block.StartUtc >= Local(14, 20)));
        Assert.IsFalse(day.Entries.Any(e => e.Evidence.Apps.Any(a => a.App == JournalStore.Paused)), "the pause itself is never shown");
    }

    [TestMethod]
    public void A_skipped_app_leaves_nothing()
    {
        Pomodoro(Local(10, 0));
        Window(9, 20, 9, 40, "Telegram", "Saved Messages");
        Window(15, 0, 15, 30, "TelegramDesktop", "Family");

        var day = _journal.Load(Day);

        Assert.IsTrue(day.Entries.Single().Evidence.IsEmpty, "Telegram is skipped by default");
        Assert.IsFalse(day.Entries.Any(e => e.OffTimer), "and starts no stretch of its own");
    }
}
