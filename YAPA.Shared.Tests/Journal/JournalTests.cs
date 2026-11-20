using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Shared.Journal;

namespace YAPA.Shared.Tests.Journal;

[TestClass]
public class JournalBuilderTests
{
    private static readonly DateTime Nine = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => Nine.AddMinutes(minutes);

    private static WorkBlock Block(int id, int start, int length = 50, int? cycle = 1) => new(id, At(start), At(start + length), length, cycle);

    [TestMethod]
    public void Evidence_lands_in_the_pomodoro_it_happened_in()
    {
        var blocks = new[] { Block(1, 0), Block(2, 60) };
        var evidence = new EvidenceSet(
            [],
            [new Commit("yapa", "a1", At(30), "Inside the first"), new Commit("yapa", "b2", At(55), "In the break after it")],
            [new Visit(At(10), "leetcode.com", "Two Sum - LeetCode"), new Visit(At(70), "react.dev", "Passing Props")],
            [new WindowSpan(At(45), At(75), "Zen", "Docs — Zen Browser")]);

        var found = JournalBuilder.Attribute(blocks, evidence);

        CollectionAssert.AreEqual(new[] { "Inside the first", "In the break after it" }, found[0].Commits.Select(c => c.Subject).ToList(),
            "a commit made in the break is the pomodoro's result");
        Assert.AreEqual(0, found[1].Commits.Count);
        Assert.AreEqual("leetcode.com", found[0].Sites.Single().Site);
        Assert.AreEqual("react.dev", found[1].Sites.Single().Site);
        // A window across the break is split at the pomodoros' edges, and its browser name dropped.
        Assert.AreEqual(5, found[0].Apps.Single().Minutes);
        Assert.AreEqual(15, found[1].Apps.Single().Minutes);
        Assert.AreEqual("Docs", found[1].Apps.Single().Titles.Single());
    }

    [TestMethod]
    public void A_conversation_counts_while_the_user_is_in_it_not_while_it_runs_on_its_own()
    {
        var blocks = new[] { Block(1, 0), Block(2, 120) };
        var session = new AgentSession("s", "interview-prep", "Mock Room", [new AgentPrompt(At(5), "add a back-end round")],
            [At(6), At(7), At(8), At(125), At(126)]);

        var found = JournalBuilder.Attribute(blocks, new EvidenceSet([session], [], [], []));

        Assert.AreEqual(4, found[0].Sessions.Single().Minutes, "the prompt's minute and three busy ones");
        Assert.AreEqual(0, found[1].Sessions.Count, "two hours after the last prompt, it's a scheduled run or one left going");
    }

    [TestMethod]
    public void A_conversation_kept_in_two_transcripts_is_one_conversation()
    {
        var prompt = new AgentPrompt(At(12), "ok i have to do hw6 next");
        var evidence = new EvidenceSet(
        [
            new AgentSession("a", "hw-5", "NLP assignment explanation", [prompt, new AgentPrompt(At(20), "start Part A")], [At(12), At(13), At(20), At(21)]),
            new AgentSession("b", "canvas_files", "NLP assignment explanation", [prompt], [At(12)]),
            new AgentSession("c", "gate-demo", "Milestone", [new AgentPrompt(At(14), "help me submit")], [At(14)]),
        ], [], [], []);

        var found = JournalBuilder.Attribute([Block(1, 0)], evidence)[0];

        Assert.AreEqual(2, found.Sessions.Count);
        Assert.AreEqual("hw-5", found.Sessions[0].Project, "the busier copy names it");
        Assert.AreEqual(2, found.Sessions[0].Prompts.Count, "each prompt once");
    }

    [TestMethod]
    public void Sign_in_pages_redirects_and_untitled_callbacks_are_not_the_work()
    {
        var visits = new[]
        {
            new Visit(At(1), "login.microsoftonline.com", "Sign in to your account"),
            new Visit(At(2), "api-6daaf5ea.duosecurity.com", "Duo"),
            new Visit(At(3), "localhost:41997", ""),
            new Visit(At(4), "stackoverflow.com", "Just a moment..."),
            new Visit(At(5), "stackoverflow.com", "How to set default Checked in checkbox ReactJS?"),
            new Visit(At(6), "github.com", "Sign in to GitHub · GitHub"),
        };

        var sites = JournalBuilder.Attribute([Block(1, 0)], new EvidenceSet([], [], visits, []))[0].Sites;

        Assert.AreEqual("stackoverflow.com", sites.Single().Site);
        Assert.AreEqual("How to set default Checked in checkbox ReactJS?", sites.Single().Titles.Single());
    }

    [TestMethod]
    public void Sessions_follow_cycles_and_fall_back_to_long_gaps()
    {
        var byCycle = JournalBuilder.Sessions([Block(1, 0, cycle: 1), Block(2, 60, cycle: 1), Block(3, 120, cycle: 2)]);
        CollectionAssert.AreEqual(new[] { 2, 1 }, byCycle.Select(s => s.Blocks.Count).ToList());

        // Logged by hand: no cycle numbers, so a 45-minute gap splits and a 10-minute one doesn't.
        var byGap = JournalBuilder.Sessions([Block(1, 0, cycle: null), Block(2, 60, cycle: null), Block(3, 155, cycle: null)]);
        CollectionAssert.AreEqual(new[] { 2, 1 }, byGap.Select(s => s.Blocks.Count).ToList());
        Assert.AreEqual(2, byGap[1].Number);
    }

    [TestMethod]
    public void Time_off_the_timer_is_stretches_of_activity_split_by_quiet_and_by_pomodoros()
    {
        var pomodoros = new[] { Block(1, 0), Block(2, 60) };
        var evidence = new EvidenceSet(
            [new AgentSession("s", "yapa", "Journal", [new AgentPrompt(At(150), "make it all day")], [At(150), At(151)])],
            [new Commit("yapa", "c1", At(55), "Made in the break: the first pomodoro's")],
            [
                new Visit(At(-30), "youtube.com", "Mafia, part 1 - YouTube"),
                new Visit(At(52), "youtube.com", "Mafia, part 2 - YouTube"),
                new Visit(At(200), "news.ycombinator.com", "Hacker News"),
                new Visit(At(205), "login.microsoftonline.com", "Sign in to your account"),
            ],
            [new WindowSpan(At(-20), At(-10), "Zen", "Mafia, part 1 — Zen Browser")]);

        var off = JournalBuilder.OffTimer(pomodoros, evidence, At(-120), At(300));

        CollectionAssert.AreEqual(new[] { -30, 52, 150, 200 }, off.Select(b => (int)(b.StartUtc - Nine).TotalMinutes).ToList(),
            "before the first, the break, and two stretches fifty quiet minutes apart; a commit claimed by a pomodoro and a sign-in page start nothing");
        Assert.AreEqual(20, off[0].Minutes, "from the first page to the end of the window");
        Assert.IsTrue(off.All(b => b.OffTimer && b.PomodoroId < 0));
        Assert.AreEqual(WorkBlock.OffTimerId(At(52)), off[1].PomodoroId, "named by the minute it starts, so its write-up and its name stay");
        Assert.AreNotEqual(WorkBlock.OffTimerId(At(52)), WorkBlock.OffTimerId(At(53)));
    }

    [TestMethod]
    public void A_glance_at_a_window_makes_no_stretch_of_its_own()
    {
        var glance = new WindowSpan(At(10), At(10).AddSeconds(4), "Zenity", "Ordinary notes");
        var stay = new WindowSpan(At(90), At(92), "Zen", "Docs");

        var off = JournalBuilder.OffTimer([], new EvidenceSet([], [], [], [glance, stay]), At(0), At(120));

        Assert.AreEqual(At(90), off.Single().StartUtc, "four seconds in front isn't time at the computer; two minutes is");
    }

    [TestMethod]
    public void Excluded_time_has_no_stretch_and_splits_the_ones_around_it()
    {
        var visits = new[] { new Visit(At(0), "a.com", "A"), new Visit(At(10), "b.com", "B"), new Visit(At(15), "c.com", "C") };
        var off = JournalBuilder.OffTimer([], new EvidenceSet([], [], visits, []), At(-60), At(60), [(At(5), At(12))]);

        Assert.AreEqual(2, off.Count, "the paused time cuts the stretch in two");
        Assert.IsTrue(off.All(b => b.EndUtc <= At(5) || b.StartUtc >= At(12)));
    }

    [TestMethod]
    public void Without_a_write_up_the_title_comes_from_the_strongest_evidence()
    {
        var conversation = new BlockEvidence([new SessionUse("p", "Interview prep strategy", 20, [])], [new Commit("r", "h", At(1), "Fix it")], [], []);
        Assert.AreEqual("Interview prep strategy", JournalBuilder.FallbackTitle(conversation));

        var commits = new BlockEvidence([], [new Commit("gate-demo", "1", At(1), "a"), new Commit("gate-demo", "2", At(2), "b")], [], []);
        Assert.AreEqual("2 commits in gate-demo", JournalBuilder.FallbackTitle(commits));

        Assert.AreEqual("", JournalBuilder.FallbackTitle(new BlockEvidence([], [], [], [])), "nothing on record says so, it doesn't make something up");
    }
}

[TestClass]
public class JournalPromptTests
{
    private static readonly DateTime Start = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Keys_tokens_and_passwords_never_leave_the_computer()
    {
        var text = "use sk-ant-oat01-AbCdEfGhIjKlMnOpQrStUv and ghp_abcdefghijklmnopqrstuvwxyz0123 then password: hunter2 " +
                   "and the jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var redacted = JournalPrompt.Redact(text);

        foreach (var secret in new[] { "sk-ant", "ghp_", "hunter2", "eyJhbGci" }) Assert.IsFalse(redacted.Contains(secret), secret);
        Assert.IsTrue(redacted.Contains("password: [secret]"));

        // Paths and ordinary words stay readable.
        const string path = "/home/arvind/Documents/NEU/CLOUD/CS6620-Fall2026-Team5-Project/infra/stack.ts";
        Assert.AreEqual(path, JournalPrompt.Redact(path));
    }

    [TestMethod]
    public void A_pomodoro_is_rewritten_only_when_its_evidence_changes()
    {
        var e = new BlockEvidence([new SessionUse("p", "t", 5, [new AgentPrompt(Start, "hi")])], [], [], []);
        Assert.AreEqual(JournalPrompt.Fingerprint(e), JournalPrompt.Fingerprint(e with { }));
        Assert.AreNotEqual(JournalPrompt.Fingerprint(e),
            JournalPrompt.Fingerprint(e with { Commits = [new Commit("r", "h", Start, "Fix")] }));
    }

    [TestMethod]
    public void The_request_has_local_times_kept_lines_and_the_users_own_titles()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var evidence = new BlockEvidence([new SessionUse("gate-demo", "Milestone", 12, [new AgentPrompt(Start.AddMinutes(3), "tear it down")], "~/Documents/NEU/CLOUD/gate-demo")],
            [], [], []);
        var blocks = new[]
        {
            new PromptBlock(new WorkBlock(1, Start, Start.AddMinutes(50), 50, 1), 1, evidence, null, null),
            new PromptBlock(new WorkBlock(2, Start.AddMinutes(60), Start.AddMinutes(110), 50, 1), 1, BlockEvidence(), new PomodoroSummary(2, "Read papers", "", []), null),
            new PromptBlock(new WorkBlock(3, Start.AddMinutes(120), Start.AddMinutes(170), 50, 1), 1, BlockEvidence(), null, "Lecture"),
        };

        var text = JournalPrompt.Build(new DateOnly(2026, 11, 18), blocks, zone);

        StringAssert.Contains(text, "Pomodoro 1 · 09:00–09:50");
        StringAssert.Contains(text, "in ~/Documents/NEU/CLOUD/gate-demo", "the folders around a project say what it's part of");
        StringAssert.Contains(text, "09:03 › tear it down");
        StringAssert.Contains(text, "(kept)\nTitle: Read papers");
        StringAssert.Contains(text, "Title from the user: \"Lecture\"");
        StringAssert.Contains(text, "Nothing on record.");
    }

    [TestMethod]
    public void Time_off_the_timer_goes_in_the_request_as_itself()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var video = new BlockEvidence([], [], [new SiteUse("youtube.com", 2, ["Mafia, part 2"])], []);
        var off = new WorkBlock(WorkBlock.OffTimerId(Start.AddMinutes(52)), Start.AddMinutes(52), Start.AddMinutes(58), 6, null, OffTimer: true);
        var blocks = new[]
        {
            new PromptBlock(new WorkBlock(1, Start, Start.AddMinutes(50), 50, 1), 1, BlockEvidence(), null, null),
            new PromptBlock(off, 1, video, null, null),
        };

        var text = JournalPrompt.Build(new DateOnly(2026, 11, 18), blocks, zone);

        StringAssert.Contains(text, "1 pomodoros, 0 h 50 min of focus", "a break isn't focus");
        StringAssert.Contains(text, $"## Off the timer {off.PomodoroId} · 09:52–09:58 · ~6 min · a break in session 1");
        StringAssert.Contains(text, "youtube.com (2): \"Mafia, part 2\"");
    }

    private static BlockEvidence BlockEvidence() => new([], [], [], []);

    [TestMethod]
    public void The_answer_is_read_and_tidied()
    {
        var answer = JsonNode.Parse("""
            {"pomodoros":[{"id":4,"title":"Fixed the **sign-in** bug.","detail":"Retried on the other sign-in.","projects":["YAPA-2",""]}],
             "sessions":[{"number":1,"title":"Assistant reliability","summary":"Made it survive a lapsed sign-in."}],
             "day":{"headline":"A YAPA day.","summary":"Mostly YAPA."}}
            """);

        var day = JournalPrompt.Parse(answer)!;

        Assert.AreEqual("Fixed the sign-in bug", day.Pomodoros[0].Title, "a title is a label: no markdown, no period");
        Assert.AreEqual("Retried on the other sign-in.", day.Pomodoros[0].Detail, "a sentence keeps its period");
        CollectionAssert.AreEqual(new[] { "YAPA-2" }, day.Pomodoros[0].Projects.ToList());
        Assert.AreEqual("A YAPA day", day.Headline);
        Assert.IsNull(JournalPrompt.Parse(JsonNode.Parse("""{"nonsense":true}""")));
    }
}

[TestClass]
public class WeekPromptTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);

    [TestMethod]
    public void A_week_is_built_from_what_its_days_already_say()
    {
        var days = new[]
        {
            new WeekDay(Monday.AddDays(1), 2, 100, "", "", [],
                [new WeekEntry("Fixed the token sk-ant-abcdefghijklmnopqrstuvwxyz in config", ["YAPA"], 50, false)]),
            new WeekDay(Monday, 6, 300, "Mock Room interview app built out", "About 4 h on Mock Room.", ["Mock Room app"],
            [
                new WeekEntry("Added the answer key", ["Mock Room"], 50, false),
                new WeekEntry("Watched videos on YouTube", [], 40, true),
            ]),
        };

        var request = WeekPrompt.Build(Monday, days);

        StringAssert.Contains(request, "8 pomodoros, 6 h 40 min of focus, on 2 days.");
        Assert.IsTrue(request.IndexOf("Monday 16 November", StringComparison.Ordinal) < request.IndexOf("Tuesday 17 November", StringComparison.Ordinal), "in order");
        StringAssert.Contains(request, "Headline: Mock Room interview app built out");
        StringAssert.Contains(request, "- Watched videos on YouTube · 40 min · off the timer");
        StringAssert.Contains(request, "- Added the answer key [Mock Room] · 50 min");
        Assert.IsFalse(request.Contains("sk-ant-abcdefghij"), "a key in a title stays on this computer");
        Assert.AreEqual(WeekPrompt.Fingerprint(request), WeekPrompt.Fingerprint(WeekPrompt.Build(Monday, days)), "the same week, the same fingerprint");
    }

    [TestMethod]
    public void The_answer_is_a_headline_and_a_summary()
    {
        var week = WeekPrompt.Parse(JsonNode.Parse("""{"headline":"Mostly **Mock Room**.","summary":"About 9 h on it."}"""));
        Assert.AreEqual(new WeekSummary("Mostly Mock Room", "About 9 h on it."), week);
        Assert.IsNull(WeekPrompt.Parse(JsonNode.Parse("""{"headline":"","summary":""}""")));
        Assert.IsNull(WeekPrompt.Parse(JsonNode.Parse("[]")));
    }
}
