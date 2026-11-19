using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Journal;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Tests.Journal;

/// <summary>A folder that stands in for the user's home, deleted afterwards.</summary>
internal sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yapa-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var full = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

[TestClass]
public class ClaudeCodeHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

    private static string Ts(int minutes) => T0.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string User(int minutes, string content, string extra = "")
        => $$"""{"parentUuid":null,"isSidechain":false,"promptId":"p","type":"user","message":{"role":"user","content":{{content}}},"uuid":"u","timestamp":"{{Ts(minutes)}}"{{extra}},"cwd":"/home/x/Code/YAPA-2","sessionId":"s1"}""";

    private static string Assistant(int minutes)
        => $$"""{"parentUuid":"u","isSidechain":false,"message":{"role":"assistant","content":[{"type":"text","text":"\"timestamp\":\"1999-01-01T00:00:00Z\""}]},"type":"assistant","uuid":"a","timestamp":"{{Ts(minutes)}}","cwd":"/home/x/Code/YAPA-2","sessionId":"s1"}""";

    [TestMethod]
    public void Reads_what_the_user_typed_and_when_the_conversation_was_busy()
    {
        using var home = new TempHome();
        var projects = home.Sub(".claude", "projects");
        var transcript = home.Sub(".claude", "projects", "-home-x-Code-YAPA-2", "s1.jsonl");
        File.WriteAllLines(transcript,
        [
            """{"type":"custom-title","customTitle":"new title","sessionId":"s1"}""",
            """{"type":"ai-title","aiTitle":"Sign-in fix for Ask YAPA","sessionId":"s1"}""",
            User(1, "\"make sure this never happens again\"", ",\"turnOrigin\":\"human\",\"promptSource\":\"sdk\""),
            User(2, """[{"type":"tool_result","tool_use_id":"t","content":"ok"}]"""),
            User(3, "\"Run the 8:30 job\"", ",\"turnOrigin\":\"scheduled\",\"promptSource\":\"system\""),
            User(4, "\"<command-name>/clear</command-name>\""),
            User(5, "\"Caveat: the messages below were generated\"", ",\"isMeta\":true"),
            Assistant(6),
            """{"parentUuid":"u","isSidechain":true,"type":"user","message":{"role":"user","content":"a subagent's task"},"timestamp":"2026-11-18T14:07:00.000Z","sessionId":"s1"}""",
        ]);
        var history = new ClaudeCodeHistory(projects, home.Sub("cache", "x"), home: "/home/x");

        var session = history.Read(T0, T0.AddHours(1)).Single();

        Assert.AreEqual("Sign-in fix for Ask YAPA", session.Title, "a placeholder title gives way to Claude Code's own");
        Assert.AreEqual("YAPA-2", session.Project);
        Assert.AreEqual("make sure this never happens again", session.Prompts.Single().Text,
            "not tool results, schedules, slash commands, hooks or subagents");
        Assert.IsTrue(session.Activity.Any(a => a == T0.AddMinutes(6)), "the top-level timestamp, not one quoted in a message");
        Assert.IsFalse(session.Activity.Any(a => a.Year == 1999));
    }

    [TestMethod]
    public void Only_what_was_appended_is_read_again_and_a_line_being_written_waits()
    {
        using var home = new TempHome();
        var projects = home.Sub(".claude", "projects");
        var transcript = home.Sub(".claude", "projects", "-p", "s1.jsonl");
        var unfinished = User(2, "\"second\"")[..40];
        File.WriteAllText(transcript, User(1, "\"first\"") + "\n" + unfinished); // the second line isn't finished
        var index = home.Sub("cache", "idx");
        var history = new ClaudeCodeHistory(projects, index, home: "/home/x");

        Assert.AreEqual(1, history.Read(T0, T0.AddHours(1)).Single().Prompts.Count);

        File.WriteAllText(transcript, User(1, "\"first\"") + "\n" + User(2, "\"second\"") + "\n" + User(3, "\"third\"") + "\n");
        // A new reader picks up the saved index and reads on from where it stopped.
        var again = new ClaudeCodeHistory(projects, index, home: "/home/x");
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, again.Read(T0, T0.AddHours(1)).Single().Prompts.Select(p => p.Text).ToList());
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(index),
            "what the user typed is indexed where only they can read it");
    }

    [TestMethod]
    public void Forgetting_scrubs_what_the_index_copied_and_leaves_the_transcript_alone()
    {
        using var home = new TempHome();
        var projects = home.Sub(".claude", "projects");
        var transcript = home.Sub(".claude", "projects", "-p", "s1.jsonl");
        var lines = new[] { User(1, "\"before\""), User(10, "\"the private one\""), User(20, "\"after\"") };
        File.WriteAllText(transcript, string.Join("\n", lines) + "\n");
        var index = home.Sub("cache", "idx");
        var history = new ClaudeCodeHistory(projects, index, home: "/home/x");
        history.Read(T0, T0.AddHours(1));

        history.Scrub(T0.AddMinutes(5), T0.AddMinutes(15));

        CollectionAssert.AreEqual(new[] { "before", "after" }, history.Read(T0, T0.AddHours(1)).Single().Prompts.Select(p => p.Text).ToList());
        var fresh = new ClaudeCodeHistory(projects, index, home: "/home/x");
        CollectionAssert.AreEqual(new[] { "before", "after" }, fresh.Read(T0, T0.AddHours(1)).Single().Prompts.Select(p => p.Text).ToList(),
            "gone from the index on disk too");
        Assert.IsFalse(Directory.GetFiles(index).Any(f => File.ReadAllText(f).Contains("the private one")));
        Assert.AreEqual(3, File.ReadAllLines(transcript).Length, "Claude Code's own transcript isn't YAPA's to change");
    }
}

[TestClass]
public class GitHistoryTests
{
    private static void Git(string dir, string args, string? date = null, string author = "Tess Ter <tess@example.com>")
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Split(' ')) info.ArgumentList.Add(a.Replace('_', ' '));
        var name = author[..author.IndexOf('<')].Trim();
        var email = author[(author.IndexOf('<') + 1)..^1];
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = name;
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = email;
        if (date != null) info.Environment["GIT_AUTHOR_DATE"] = info.Environment["GIT_COMMITTER_DATE"] = date;
        using var p = Process.Start(info)!;
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, p.StandardError.ReadToEnd());
    }

    [TestMethod]
    public void Finds_the_users_own_commits_in_the_range_in_every_repository()
    {
        using var home = new TempHome();
        var global = home.Sub(".gitconfig");
        File.WriteAllText(global, "[user]\n\tname = Tess Ter\n\temail = tess@example.com\n");
        var saved = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        try
        {
            var repo = Path.GetDirectoryName(home.Sub("Code", "gate-demo", "x"))!;
            Git(repo, "init -q");
            Git(repo, "commit -q --allow-empty -m Mine_inside", "2026-11-18T14:30:00Z");
            Git(repo, "commit -q --allow-empty -m Teammate", "2026-11-18T14:40:00Z", "Someone Else <else@example.com>");
            Git(repo, "commit -q --allow-empty -m Mine_later", "2026-11-18T18:00:00Z");
            var hidden = Path.GetDirectoryName(home.Sub(".cache", "tool", "x"))!;
            Git(hidden, "init -q");
            Git(hidden, "commit -q --allow-empty -m In_a_cache", "2026-11-18T14:35:00Z");

            var git = new GitHistory(home.Path, home.Sub("cache", "repos.json"));
            var from = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
            var commits = git.Read(from, from.AddHours(2), []);

            Assert.AreEqual("Mine inside", commits.Single().Subject, "not a teammate's, not out of range, not in a hidden folder");
            Assert.AreEqual("gate-demo", commits[0].Repo);
            Assert.AreEqual(from.AddMinutes(30), commits[0].AtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", saved);
        }
    }
}

[TestClass]
public class BrowserHistoryTests
{
    private static void Sql(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [TestMethod]
    public void Reads_firefox_and_chrome_history_with_their_own_clocks()
    {
        using var home = new TempHome();
        var at = new DateTimeOffset(2026, 11, 18, 14, 10, 0, TimeSpan.Zero);
        var firefoxMicros = at.ToUnixTimeMilliseconds() * 1000;
        var chromeMicros = (at.ToUnixTimeSeconds() + 11_644_473_600) * 1_000_000 + 60_000_000; // a minute later

        var places = home.Sub(".zen", "abc.Default (release)", "places.sqlite");
        Sql(places, $"""
            CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER, visit_date INTEGER);
            INSERT INTO moz_places VALUES (1, 'https://www.leetcode.com/problems/two-sum/', 'Two Sum - LeetCode'), (2, 'about:newtab', 'New Tab');
            INSERT INTO moz_historyvisits VALUES (1, 1, {firefoxMicros}), (2, 2, {firefoxMicros}), (3, 1, {firefoxMicros - 86_400_000_000});
            """);
        var chrome = home.Sub(".config", "google-chrome", "Default", "History");
        Sql(chrome, $"""
            CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            CREATE TABLE visits (id INTEGER PRIMARY KEY, url INTEGER, visit_time INTEGER);
            INSERT INTO urls VALUES (1, 'http://127.0.0.1:8765/history', 'History – Mock Room');
            INSERT INTO visits VALUES (1, 1, {chromeMicros});
            """);

        var scratch = System.IO.Path.Combine(home.Path, "cache", "scratch");
        var visits = new BrowserHistory(home.Path, scratch).Read(at.UtcDateTime.AddMinutes(-5), at.UtcDateTime.AddMinutes(5));

        Assert.AreEqual(2, visits.Count, "not the new tab page, not the day before");
        Assert.AreEqual(("leetcode.com", "Two Sum - LeetCode", at.UtcDateTime), (visits[0].Site, visits[0].Title, visits[0].AtUtc));
        Assert.AreEqual(("127.0.0.1:8765", at.UtcDateTime.AddMinutes(1)), (visits[1].Site, visits[1].AtUtc));
        Assert.IsTrue(!Directory.Exists(scratch) || Directory.GetDirectories(scratch).Length == 0, "the copies are gone");
    }

    [TestMethod]
    public void An_unchanged_history_is_answered_from_the_last_read_and_a_changed_one_is_read_again()
    {
        using var home = new TempHome();
        var now = DateTime.UtcNow;
        long Micros(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds() * 1000;
        var places = home.Sub(".mozilla", "firefox", "x.default", "places.sqlite");
        Sql(places, $"""
            CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER, visit_date INTEGER);
            INSERT INTO moz_places VALUES (1, 'https://a.example/', 'A');
            INSERT INTO moz_historyvisits VALUES (1, 1, {Micros(now.AddMinutes(-30))});
            """);
        var scratch = System.IO.Path.Combine(home.Path, "scratch");
        // Left by a crash: a copy of someone's history doesn't stay.
        Directory.CreateDirectory(System.IO.Path.Combine(scratch, "leftover"));
        var browsers = new BrowserHistory(home.Path, scratch);

        Assert.AreEqual(1, browsers.Read(now.AddHours(-1), now).Count);
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(scratch, "leftover")));
        Assert.AreEqual(1, browsers.Read(now.AddMinutes(-45), now.AddMinutes(1)).Count, "a narrower range up to now comes from what was read");

        Sql(places, $"INSERT INTO moz_places VALUES (2, 'https://b.example/', 'B'); INSERT INTO moz_historyvisits VALUES (2, 2, {Micros(now.AddMinutes(-1))});");
        File.SetLastWriteTimeUtc(places, now.AddSeconds(5)); // as a browser's write would
        CollectionAssert.AreEqual(new[] { "a.example", "b.example" }, browsers.Read(now.AddHours(-1), now).Select(v => v.Site).ToList(),
            "the browser wrote: read again");
    }
}

[TestClass]
public class JournalStoreTests
{
    [TestMethod]
    public void Keeps_windows_titles_and_write_ups_and_forgets_them_on_request()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var t = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

        var id = store.BeginSpan(t, "Zen", "Docs");
        store.ExtendSpan(id, t.AddMinutes(4));
        Assert.AreEqual(new WindowSpan(t, t.AddMinutes(4), "Zen", "Docs"), store.Spans(t.AddMinutes(1), t.AddMinutes(2)).Single());

        store.SetTitle(7, "  Lecture  ");
        Assert.AreEqual("Lecture", store.Titles([7, 8])[7]);
        store.SetTitle(7, "");
        Assert.AreEqual(0, store.Titles([7]).Count, "blank gives it back to the write-up");

        var summary = new DaySummary("A day", "Mostly work.", [new SessionSummary(1, "Morning", "Things.")], [new PomodoroSummary(7, "Did it", "", ["YAPA"])]);
        store.SaveDay(new DateOnly(2026, 11, 18), summary, new System.Collections.Generic.Dictionary<int, string> { [7] = "abc" });
        var stored = store.Day(new DateOnly(2026, 11, 18))!;
        Assert.AreEqual("Did it", stored.Summary.Pomodoros.Single().Title);
        Assert.AreEqual("abc", stored.Fingerprints[7]);

        store.Forget();
        Assert.IsNull(store.Day(new DateOnly(2026, 11, 18)));
        Assert.AreEqual(0, store.SpanCount());
    }

    [TestMethod]
    public void A_days_windows_are_found_by_their_start_including_one_from_the_night_before()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var day = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        store.ExtendSpan(store.BeginSpan(day.AddHours(-3), "mpv", "A long film"), day.AddHours(1));
        store.ExtendSpan(store.BeginSpan(day.AddDays(-2), "Zen", "Long ago"), day.AddDays(-2).AddHours(1));
        store.ExtendSpan(store.BeginSpan(day.AddHours(9), "Zen", "Docs"), day.AddHours(10));

        CollectionAssert.AreEqual(new[] { "A long film", "Docs" }, store.Spans(day, day.AddDays(1)).Select(s => s.Title).ToList());
    }

    [TestMethod]
    public void Deleting_a_range_cuts_what_crosses_it_and_drops_what_is_inside()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var t = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
        void Span(int from, int to, string title) => store.ExtendSpan(store.BeginSpan(t.AddMinutes(from), "Zen", title), t.AddMinutes(to));
        Span(0, 15, "into it");
        Span(20, 25, "inside");
        Span(28, 40, "out of it");
        Span(50, 60, "after");
        Span(70, 100, "around it");

        store.DeleteSpans(t.AddMinutes(10), t.AddMinutes(30));
        store.DeleteSpans(t.AddMinutes(80), t.AddMinutes(90));

        var left = store.Spans(t, t.AddHours(2)).Select(s => (s.Title, From: (int)(s.StartUtc - t).TotalMinutes, To: (int)(s.EndUtc - t).TotalMinutes)).ToList();
        CollectionAssert.AreEqual(new[]
        {
            ("into it", 0, 10), ("out of it", 30, 40), ("after", 50, 60), ("around it", 70, 80), ("around it", 90, 100),
        }, left);
    }

    [TestMethod]
    public void Time_off_the_record_outlasts_forgetting_everything()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var t = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
        store.AddOffRecord(t, t.AddHours(1), OffRecordRange.Forgotten);

        store.Forget();

        Assert.AreEqual(1, store.OffRecord(t, t.AddDays(1)).Count, "the browsers still have that time: only the range keeps it out");
        Assert.AreEqual(0, store.OffRecord(t.AddHours(2), t.AddDays(1)).Count);
    }
}

[TestClass]
public class OffTheRecordTests
{
    [TestMethod]
    public void Goes_off_for_a_while_comes_back_and_remembers_across_a_restart()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var now = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
        using var off = new OffTheRecord(store, () => now);
        var changes = 0;
        off.Changed += () => changes++;

        off.Start(TimeSpan.FromHours(1));
        Assert.AreEqual(now.AddHours(1), off.Until);
        using (var restarted = new OffTheRecord(store, () => now.AddMinutes(5)))
            Assert.AreEqual(now.AddHours(1), restarted.Until, "YAPA restarted in the middle: still off");

        off.Start(null);
        Assert.AreEqual(OffTheRecord.Indefinitely, off.Until, "the end moves; no second range");
        Assert.AreEqual(1, store.OffRecord(now, now.AddDays(1)).Count);

        now = now.AddMinutes(20);
        off.Stop();
        Assert.IsFalse(off.IsActive);
        Assert.AreEqual(now, store.OffRecord(now.AddHours(-1), now).Single().EndUtc, "what was off the record stays off it");
        Assert.AreEqual(3, changes);

        off.Restore(OffTheRecord.Indefinitely);
        Assert.AreEqual(OffTheRecord.Indefinitely, off.Until, "undo puts it back");
    }

    [TestMethod]
    public void Its_time_runs_out_on_its_own()
    {
        using var db = new Persistence.TempDatabase();
        var store = new JournalStore(db.FilePath);
        var now = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
        using var off = new OffTheRecord(store, () => now);
        off.Start(TimeSpan.FromMinutes(30));
        now = now.AddMinutes(31);
        Assert.IsFalse(off.IsActive);
    }
}

[TestClass]
public class AdultSitesTests
{
    [TestMethod]
    public void Adult_sites_are_known_by_the_list_their_names_and_their_titles()
    {
        Assert.IsTrue(AdultSites.Count > 5000);
        foreach (var host in new[] { "pornhub.com", "www.xvideos.com", "de.xhamster.com", "onlyfans.com", "erome.com", "fetlife.com", "some-new-porn-site.example" })
            Assert.IsTrue(AdultSites.IsAdultHost(host), host);
        foreach (var title in new[] { "Hot stuff - Pornhub.com", "xHamster — Zen Browser", "r/nsfw", "pornpics.com - gallery", "Rule 34 :: tag" })
            Assert.IsTrue(AdultSites.IsAdultText(title), title);
    }

    [TestMethod]
    public void Mainstream_sites_and_ordinary_words_are_not()
    {
        foreach (var host in new[] { "github.com", "youtube.com", "character.ai", "cosmopolitan.com", "yes24.com", "tumblr.com", "reddit.com", "xxxlutz.de",
                     "essexpc.net", "guitarfetish.com", "sussex.ac.uk", "webcamgalore.com", "tinder.com" })
            Assert.IsFalse(AdultSites.IsAdultHost(host), host);
        foreach (var title in new[] { "Middlesex University", "JavaScript - MDN", "Sextant navigation", "ActivityRecorder.cs — YAPA-2", "Essex weather" })
            Assert.IsFalse(AdultSites.IsAdultText(title), title);
    }

    [TestMethod]
    public void Every_listed_host_is_a_plain_lowercase_name()
    {
        foreach (var host in AdultSites.Listed)
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(host, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9-]+)+$"), host);
    }
}

[TestClass]
public class ActivityRecorderTests
{
    [TestMethod]
    public void Apps_are_named_as_people_call_them()
    {
        Assert.AreEqual("Claude", ActivityRecorder.AppName(new ActiveWindow("com.anthropic.Claude", "com.anthropic.claude", "Claude", 1)));
        Assert.AreEqual("Zen", ActivityRecorder.AppName(new ActiveWindow("zen", "Navigator", "x", 1)));
        Assert.AreEqual("Browser (driven by an agent)",
            ActivityRecorder.AppName(new ActiveWindow("Google-chrome", "google-chrome (/home/a/.cache/ms-playwright-mcp/mcp-chrome-1)", "x", 1)));
        Assert.AreEqual("TextEditor", ActivityRecorder.AppName(new ActiveWindow("org.gnome.TextEditor", "gnome-text-editor", "x", 1)));
        Assert.AreEqual("Telegram", ActivityRecorder.AppName(new ActiveWindow("TelegramDesktop", "telegram-desktop", "x", 1)));
        Assert.IsTrue(ActivityRecorder.IsDesktop(new ActiveWindow("Gjs", "gjs", "Desktop Icons 1", 1)), "the bare desktop isn't an app");
        Assert.IsFalse(ActivityRecorder.IsDesktop(new ActiveWindow("Gjs", "gjs", "Extension preferences", 1)));
    }
}

[TestClass]
public class PausingAppsTests
{
    private static readonly string[] Tor = [PausingApps.Squash("Tor Browser")];

    [TestMethod]
    public void Tor_Browser_is_found_however_it_was_installed_and_nothing_else_is()
    {
        Assert.IsTrue(PausingApps.Names("/home/a/.local/share/torbrowser/tbb/x86_64/tor-browser/Browser/firefox.real", Tor), "torbrowser-launcher's copy");
        Assert.IsTrue(PausingApps.Names("/home/a/Downloads/tor-browser/Browser/TorBrowser/Tor/tor", Tor), "the tarball, and its tor");
        Assert.IsTrue(PausingApps.Names("/usr/bin/torbrowser-launcher", Tor));
        Assert.IsTrue(PausingApps.Names("/opt/Tor Browser/Browser/firefox", Tor));
        Assert.IsFalse(PausingApps.Names("/usr/bin/firefox", Tor));
        Assert.IsFalse(PausingApps.Names("/usr/bin/editor", [PausingApps.Squash("tor")]), "a name matches the start of a part, not anywhere");
    }

    [TestMethod]
    public void Running_programs_are_read_from_proc()
    {
        using var home = new TempHome();
        void Process(string pid, string argv0)
        {
            Directory.CreateDirectory(Path.Combine(home.Path, pid));
            File.WriteAllText(Path.Combine(home.Path, pid, "cmdline"), argv0 + "\0--some-arg\0");
        }
        Process("100", "/usr/bin/zen");
        Process("self", "/home/a/tor-browser/Browser/firefox.real"); // not a process
        Assert.IsFalse(PausingApps.AnyRunning(["Tor Browser"], home.Path));

        Process("4242", "/home/a/tor-browser/Browser/firefox.real");
        Assert.IsTrue(PausingApps.AnyRunning(["Tor Browser"], home.Path));
        Assert.IsFalse(PausingApps.AnyRunning([], home.Path), "an empty list pauses nothing");
    }

    [TestMethod]
    public void Known_processes_are_read_again_until_they_settle_then_not_at_all()
    {
        using var home = new TempHome();
        var cmdline = Path.Combine(home.Path, "77", "cmdline");
        Directory.CreateDirectory(Path.GetDirectoryName(cmdline)!);
        var known = new Dictionary<string, PausingApps.KnownProgram>();
        var t0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        // A launcher script that execs the browser a moment later, keeping its id.
        File.WriteAllText(cmdline, "/bin/bash\0/home/a/start-tor-browser\0");
        Assert.IsFalse(PausingApps.AnyRunning(["Tor Browser"], home.Path, known, t0));
        File.WriteAllText(cmdline, "/home/a/tor-browser/Browser/firefox.real\0");
        Assert.IsTrue(PausingApps.AnyRunning(["Tor Browser"], home.Path, known, t0.AddSeconds(5)), "still young: read again, and found");

        // Settled: taken from memory, not read (the file changing proves it isn't).
        File.WriteAllText(cmdline, "/usr/bin/zen\0");
        Assert.IsTrue(PausingApps.AnyRunning(["Tor Browser"], home.Path, known, t0.AddMinutes(1)));

        Directory.Delete(Path.GetDirectoryName(cmdline)!, recursive: true);
        Assert.IsFalse(PausingApps.AnyRunning(["Tor Browser"], home.Path, known, t0.AddMinutes(2)));
        Assert.AreEqual(0, known.Count, "a process that's gone is forgotten");
    }
}
