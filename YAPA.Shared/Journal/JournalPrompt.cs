#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YAPA.Shared.Journal;

/// <param name="Projects">Short names of what it belongs to, the same name for the same thing all day.</param>
public sealed record PomodoroSummary(int PomodoroId, string Title, string Detail, IReadOnlyList<string> Projects);

public sealed record SessionSummary(int Number, string Title, string Summary);

public sealed record DaySummary(string Headline, string Summary, IReadOnlyList<SessionSummary> Sessions, IReadOnlyList<PomodoroSummary> Pomodoros);

/// <summary>One pomodoro as it goes to Claude.</summary>
/// <param name="Kept">Already written, and its evidence unchanged: sent as context, not to be written again.</param>
/// <param name="UserTitle">What the user called it; it stands.</param>
public sealed record PromptBlock(WorkBlock Block, int Session, BlockEvidence Evidence, PomodoroSummary? Kept, string? UserTitle);

/// <summary>
/// Turns a day's evidence into the request for its journal, and reads the answer. The
/// request carries what was on record in each pomodoro and each stretch off the timer, with
/// anything that looks like a secret blanked out.
/// </summary>
public static class JournalPrompt
{
    private const int PromptChars = 280;

    /// <summary>
    /// How the write-ups are written. Part of every fingerprint, so a change of style rewrites each
    /// day once, when it's next opened, instead of leaving old days in the old voice.
    /// </summary>
    public const string Style = "plain-2";

    public const string System = """
        You write a short work journal from evidence. The user works in pomodoros (focused work intervals). Later they glance at the page to remember what they did. Write so a glance is enough.

        You also get the stretches "off the timer": time at the computer outside pomodoros. They have negative ids.

        For each pomodoro you get what was on record during it: Claude Code conversations the user typed into (title, project folder, the user's own prompts), git commits, web pages visited, and the windows in front. From that, say what the user did.

        Voice
        - Everyday words, like a friend summing up the day. No jargon, no abbreviations the user didn't use, no file names, URLs, branch names, model or tool names. Name a tool only when the work was about the tool itself.
        - Say what was done, not how: "Fixed the sign-in bug", not "Asked Claude to fix auth via a PR".
        - Only claim what the evidence shows: "Worked on" unless commits, prompts or pages show it finished. Thin or scattered evidence gets a few honest words ("Email and admin").
        - Ignore noise: sign-in pages, a single search, the pomodoro app itself.
        - No praise, no judgement, no advice, no markdown. Never "the user" or a pronoun: write like a log, starting with the verb.

        Lengths (hard limits: shorter is better)
        - Pomodoro title: at most 6 words, sentence case, no final period. A verb or a noun phrase: "Practised graph problems", "Mock Room video interviews".
        - Pomodoro detail: one short sentence, at most 15 words, the single specific worth remembering. Empty when the title says it all.
        - Projects: 1 or 2 short, plain names in sentence case ("Interview prep", "NLP homework", "Job search"), never a repository slug like "interview-prep". When a name in "Names already in use" fits, use it exactly as written there. Never a catch-all ("Misc", "Other", "Browsing"): a scattered pomodoro gets none.
        - Off the timer: the same way, plainly, whatever it was ("Watched YouTube", "Replied to recruiters"). It is not focus time: never call it a pomodoro.
        - A pomodoro with no evidence at all: empty title and detail.
        - Session (a run of pomodoros between long breaks): title at most 4 words; summary one sentence, at most 15 words.
        - Day headline: at most 8 words, what the day was mostly about.
        - Day summary: one or two short sentences, at most 30 words: what mattered most and what got done. No hour counts or lists of times: the page shows those.

        Pomodoros and stretches marked "kept" are already written: use them as context and don't return them. A title "from the user" is authoritative: return it unchanged, write its detail, and build the session and the day around it.
        """;

    public static string Schema => """
        {"type":"object","additionalProperties":false,"required":["pomodoros","sessions","day"],"properties":{
          "pomodoros":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","title","detail","projects"],"properties":{
            "id":{"type":"integer"},"title":{"type":"string"},"detail":{"type":"string"},"projects":{"type":"array","items":{"type":"string"}}}}},
          "sessions":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["number","title","summary"],"properties":{
            "number":{"type":"integer"},"title":{"type":"string"},"summary":{"type":"string"}}}},
          "day":{"type":"object","additionalProperties":false,"required":["headline","summary"],"properties":{
            "headline":{"type":"string"},"summary":{"type":"string"}}}}}
        """;

    /// <param name="local">The user's time zone: everything is written in their clock's time.</param>
    /// <param name="windowsRecorded">
    /// Whether the windows in front were recorded that day. Without them, time off the timer is
    /// only the moments something else left a trace, and says nothing about how long it was.
    /// </param>
    /// <param name="knownProjects">Project names written on earlier days, the most used first: reused, so a project keeps one name.</param>
    public static string Build(DateOnly day, IReadOnlyList<PromptBlock> blocks, TimeZoneInfo local, bool windowsRecorded = true,
        IReadOnlyList<string>? knownProjects = null)
    {
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, local).ToString("HH:mm", CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.Append("Day: ").Append(day.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture))
            .Append(". Times are the user's local time, 24-hour.\n");
        var pomodoros = blocks.Where(b => !b.Block.OffTimer).ToList();
        var offTimer = blocks.Where(b => b.Block.OffTimer).ToList();
        var totalMinutes = pomodoros.Sum(b => b.Block.Minutes);
        text.Append(CultureInfo.InvariantCulture, $"{pomodoros.Count} pomodoros, {totalMinutes / 60} h {totalMinutes % 60} min of focus, in {pomodoros.Select(b => b.Session).Distinct().Count()} sessions.\n");
        if (offTimer.Count > 0)
        {
            var offMinutes = offTimer.Sum(b => b.Block.Minutes);
            text.Append(windowsRecorded
                ? string.Create(CultureInfo.InvariantCulture, $"{offTimer.Count} stretches off the timer, about {offMinutes / 60} h {offMinutes % 60} min at the computer.\n")
                : string.Create(CultureInfo.InvariantCulture, $"{offTimer.Count} stretches off the timer. The windows in front weren't recorded this day, so these are only the moments a page, prompt or commit left a trace: say nothing about how long the user was at the computer off the timer.\n"));
        }

        if (knownProjects is { Count: > 0 })
            text.Append("Names already in use: ").Append(string.Join(", ", knownProjects.Select(p => Clip(Redact(p), 40)))).Append(".\n");

        foreach (var b in blocks)
        {
            var heading = b.Block.OffTimer
                ? $"## Off the timer {b.Block.PomodoroId} · {Time(b.Block.StartUtc)}–{Time(b.Block.EndUtc)} · ~{b.Block.Minutes} min · " +
                  (b.Session > 0 ? $"a break in session {b.Session}" : "outside the sessions")
                : $"## Pomodoro {b.Block.PomodoroId} · {Time(b.Block.StartUtc)}–{Time(b.Block.EndUtc)} · {b.Block.Minutes} min · session {b.Session}";
            text.Append('\n').Append(heading);
            if (b.Kept is { } kept)
            {
                text.Append(" (kept)\n").Append("Title: ").Append(b.UserTitle ?? kept.Title).Append('\n');
                if (kept.Detail.Length > 0) text.Append("Detail: ").Append(kept.Detail).Append('\n');
                continue;
            }
            text.Append('\n');
            if (b.UserTitle != null) text.Append("Title from the user: ").Append(Quote(b.UserTitle)).Append('\n');
            text.Append(Describe(b.Evidence, Time));
        }
        return text.ToString();
    }

    /// <summary>The evidence of one pomodoro as lines of text; also what its fingerprint is made of.</summary>
    public static string Describe(BlockEvidence e, Func<DateTime, string> time)
    {
        if (e.IsEmpty) return "Nothing on record.\n";
        var text = new StringBuilder();
        foreach (var s in e.Sessions)
        {
            text.Append("Claude Code conversation ").Append(Quote(s.Title));
            if (s.Place.Length > 0) text.Append(" in ").Append(s.Place);
            else if (s.Project.Length > 0) text.Append(" in ").Append(s.Project);
            text.Append(CultureInfo.InvariantCulture, $", in use ~{s.Minutes} min").Append(s.Prompts.Count > 0 ? ". The user typed:\n" : ", no prompts typed in this pomodoro.\n");
            foreach (var p in s.Prompts) text.Append("  ").Append(time(p.AtUtc)).Append(" › ").Append(Clip(Redact(p.Text), PromptChars)).Append('\n');
        }
        if (e.Commits.Count > 0)
        {
            text.Append("Commits:\n");
            foreach (var c in e.Commits) text.Append("  ").Append(time(c.AtUtc)).Append(' ').Append(c.Repo).Append(": ").Append(Quote(Clip(Redact(c.Subject), 160))).Append('\n');
        }
        if (e.Sites.Count > 0)
        {
            text.Append("Pages:\n");
            foreach (var s in e.Sites)
                text.Append(CultureInfo.InvariantCulture, $"  {s.Site} ({s.Visits}): ")
                    .Append(string.Join(", ", s.Titles.Select(t => Quote(Clip(Redact(t), 90))))).Append('\n');
        }
        if (e.Apps.Count > 0)
        {
            text.Append("Windows in front:\n");
            foreach (var a in e.Apps)
                text.Append(CultureInfo.InvariantCulture, $"  {a.App} {a.Minutes} min")
                    .Append(a.Titles.Count > 0 ? ": " + string.Join(", ", a.Titles.Select(t => Quote(Clip(Redact(t), 90)))) : "").Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A short, stable fingerprint of a pomodoro's evidence: its summary is redone only when this changes.</summary>
    public static string Fingerprint(BlockEvidence e)
    {
        var text = Style + "\n" + Describe(e, t => t.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>The answer, from Claude Code's structured output; null when it isn't one.</summary>
    public static DaySummary? Parse(JsonNode? answer)
    {
        if (answer is not JsonObject o || o["day"] is not JsonObject day) return null;
        try
        {
            var pomodoros = (o["pomodoros"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => new PomodoroSummary(
                    p["id"]!.GetValue<int>(),
                    TidyTitle(p["title"]?.GetValue<string>()),
                    Tidy(p["detail"]?.GetValue<string>()),
                    (p["projects"] as JsonArray ?? []).Select(x => Tidy(x?.GetValue<string>())).Where(x => x.Length > 0).Take(3).ToList()))
                .ToList();
            var sessions = (o["sessions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => new SessionSummary(s["number"]!.GetValue<int>(), TidyTitle(s["title"]?.GetValue<string>()), Tidy(s["summary"]?.GetValue<string>())))
                .ToList();
            return new DaySummary(TidyTitle(day["headline"]?.GetValue<string>()), Tidy(day["summary"]?.GetValue<string>()), sessions, pomodoros);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException or JsonException)
        {
            return null;
        }
    }

    private static string Tidy(string? s) => Regex.Replace((s ?? "").Replace("**", ""), @"\s+", " ").Trim();

    // A title is a label, not a sentence.
    private static string TidyTitle(string? s) => Tidy(s).TrimEnd('.');

    private static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";

    private static string Clip(string s, int max)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    // Keys, tokens and passwords pasted into a prompt or a title stay on this computer.
    private static readonly Regex Secrets = new(
        @"sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9\-]{10,}" +
        @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}|\b[A-Fa-f0-9]{32,}\b" +
        // A long run of mixed case and digits is a key; paths and slugs (slashes, words) aren't.
        @"|\b(?=[A-Za-z0-9+_\-]*\d)(?=[A-Za-z0-9+_\-]*[A-Z])(?=[A-Za-z0-9+_\-]*[a-z])[A-Za-z0-9+_\-]{40,}={0,2}" +
        @"|(?i:(?<label>password|passwd|secret|token|api[_ ]?key)\s*[:=]\s*)\S+",
        RegexOptions.Compiled);

    public static string Redact(string text)
        => Secrets.Replace(text, m => m.Groups["label"].Success ? m.Groups["label"].Value + ": [secret]" : "[secret]");
}
