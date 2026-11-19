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
/// request carries only what happened inside pomodoros, with anything that looks like a
/// secret blanked out.
/// </summary>
public static class JournalPrompt
{
    private const int PromptChars = 280;

    public const string System = """
        You write a work journal from evidence. The user works in pomodoros (focused work intervals) and later wants to remember what they did in each one, and whether the day went anywhere.

        For each pomodoro you get what was on record during it: Claude Code conversations the user typed into (the conversation's title, its project folder, and the user's own prompts), git commits, web pages visited, and the windows in front. From that, say what the user actually worked on.

        How to write
        - Name the work, not the tool. Say which assignment, bug, feature, company, problem or document: "Debugged the Lambda cold start in gate-demo", not "Worked in Claude Code". Never make a tool the subject unless the work was about it.
        - Infer the task from the prompts, titles and pages; don't list the evidence back.
        - Only claim what the evidence supports. "Worked on" when nothing shows it finished; "fixed", "finished", "submitted" or "shipped" only when commits, prompts or pages show it. When the evidence is thin or scattered, say so in a few plain words ("Admin: Canvas, Workday and email").
        - A pomodoro with no evidence at all gets an empty title and detail: the user was away from the computer, or it wasn't recorded.
        - Ignore noise: sign-in pages, a single search, the pomodoro app itself.
        - Pomodoro title: sentence case, at most 8 words, no final period. Start with a past-tense verb or a noun phrase.
        - Pomodoro detail: one sentence of at most 25 words with the concrete specifics (what was decided, built, found or read). Empty when the title says it all.
        - Projects: 1 to 3 short names of what the pomodoro belongs to (a course, a repository, the job search, interview prep). Use the same name, spelled the same way, for the same thing all day. Never a catch-all such as "Miscellaneous", "Other", "Browsing" or "General": a scattered pomodoro gets no project.
        - Session: a run of pomodoros between long breaks. Title at most 6 words; summary one or two sentences, at most 35 words.
        - Day headline: at most 12 words, the shape of the day. Day summary: 2 or 3 sentences, at most 70 words: where the time went, by project with rough hours, and what got done.
        - Plain and factual. No praise, no judgement, no advice, no markdown. Use the user's own words for things where you can.
        - Pomodoros marked "kept" are already written: use them as context and don't return them. A title "from the user" is authoritative: return it unchanged, write its detail, and build the session and the day around it.
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
    public static string Build(DateOnly day, IReadOnlyList<PromptBlock> blocks, TimeZoneInfo local)
    {
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, local).ToString("HH:mm", CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.Append("Day: ").Append(day.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture))
            .Append(". Times are the user's local time, 24-hour.\n");
        var totalMinutes = blocks.Sum(b => b.Block.Minutes);
        text.Append(CultureInfo.InvariantCulture, $"{blocks.Count} pomodoros, {totalMinutes / 60} h {totalMinutes % 60} min of focus, in {blocks.Select(b => b.Session).Distinct().Count()} sessions.\n");

        foreach (var b in blocks)
        {
            text.Append('\n').Append(CultureInfo.InvariantCulture,
                $"## Pomodoro {b.Block.PomodoroId} · {Time(b.Block.StartUtc)}–{Time(b.Block.EndUtc)} · {b.Block.Minutes} min · session {b.Session}");
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
        var text = Describe(e, t => t.ToString("O", CultureInfo.InvariantCulture));
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
