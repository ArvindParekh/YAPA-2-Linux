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

/// <summary>One thing done in a day, as the week sees it: what it was, what it belongs to, how long.</summary>
/// <param name="OffTimer">Time at the computer outside a pomodoro: there, but not focus time.</param>
public sealed record WeekEntry(string Title, IReadOnlyList<string> Projects, int Minutes, bool OffTimer);

/// <summary>A day of the week, from what its journal already holds.</summary>
/// <param name="Headline">The day's written headline; empty when the day isn't written up.</param>
public sealed record WeekDay(DateOnly Date, int Pomodoros, int FocusMinutes, string Headline, string Summary,
    IReadOnlyList<string> Sessions, IReadOnlyList<WeekEntry> Entries);

public sealed record WeekSummary(string Headline, string Summary);

/// <summary>
/// The request for a week's write-up, and its answer. Built from the days' journals (their
/// write-ups, and the titles of what was done), not from the raw evidence again: a week is
/// one short request, and says only what the days already say.
/// </summary>
public static class WeekPrompt
{
    /// <summary>A day lists at most this many of its entries, the longest first.</summary>
    private const int EntriesPerDay = 12;

    public const string System = """
        You write the week's entry in a short work journal, for a glance. You get each day as its journal has it: pomodoros (focused work intervals) and focus time, the day's headline and summary when written, the sessions, and the things done with their projects and minutes. Entries marked "off the timer" were at the computer outside a pomodoro: there, but not focus time.

        Write
        - Headline: at most 8 words, what the week was mostly about.
        - Summary: two short sentences, at most 40 words: what the week went to, and what got done, by name. No hour counts or day-by-day lists: the page shows those.
        - Everyday words, no jargon, no tool, file or site names unless they are the work itself. Use the projects' names as the days spell them.
        - Only what the days say: "Worked on" unless a day says it was finished.
        - No praise, no judgement, no advice, no markdown. Never "the user" or a pronoun: write like a log.
        """;

    public static string Schema => """
        {"type":"object","additionalProperties":false,"required":["headline","summary"],"properties":{
          "headline":{"type":"string"},"summary":{"type":"string"}}}
        """;

    /// <param name="start">The first day of the week.</param>
    /// <param name="days">The week's days up to today, any order; days with nothing on record may be left out.</param>
    public static string Build(DateOnly start, IReadOnlyList<WeekDay> days)
    {
        var text = new StringBuilder();
        var end = start.AddDays(6);
        var focus = days.Sum(d => d.FocusMinutes);
        text.Append(CultureInfo.InvariantCulture, $"# The week of {start:dddd d MMMM yyyy} to {end:dddd d MMMM}\n");
        text.Append(CultureInfo.InvariantCulture, $"{days.Sum(d => d.Pomodoros)} pomodoros, {Hours(focus)} of focus, on {days.Count(d => d.Pomodoros > 0)} days.\n");
        foreach (var day in days.OrderBy(d => d.Date))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {day.Date:dddd d MMMM} · {day.Pomodoros} pomodoros · {Hours(day.FocusMinutes)} of focus\n");
            if (day.Headline.Length > 0) text.Append("Headline: ").Append(Clean(day.Headline, 120)).Append('\n');
            if (day.Summary.Length > 0) text.Append("Summary: ").Append(Clean(day.Summary, 600)).Append('\n');
            if (day.Sessions.Count > 0) text.Append("Sessions: ").Append(string.Join("; ", day.Sessions.Select(s => Clean(s, 80)))).Append('\n');
            foreach (var e in day.Entries.Where(e => e.Title.Length > 0).OrderByDescending(e => e.Minutes).Take(EntriesPerDay))
            {
                text.Append("- ").Append(Clean(e.Title, 100));
                if (e.Projects.Count > 0) text.Append(" [").Append(string.Join(", ", e.Projects.Select(p => Clean(p, 40)))).Append(']');
                text.Append(CultureInfo.InvariantCulture, $" · {e.Minutes} min");
                if (e.OffTimer) text.Append(" · off the timer");
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    /// <summary>A short, stable fingerprint of the request: the week is rewritten only when what it's built from changes.</summary>
    public static string Fingerprint(string request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JournalPrompt.Style + "\n" + request)))[..16].ToLowerInvariant();

    /// <summary>The answer, from Claude Code's structured output; null when it isn't one.</summary>
    public static WeekSummary? Parse(JsonNode? answer)
    {
        if (answer is not JsonObject o) return null;
        try
        {
            var headline = Tidy(o["headline"]?.GetValue<string>()).TrimEnd('.');
            var summary = Tidy(o["summary"]?.GetValue<string>());
            return headline.Length == 0 && summary.Length == 0 ? null : new WeekSummary(headline, summary);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Hours(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private static string Tidy(string? s) => Regex.Replace((s ?? "").Replace("**", ""), @"\s+", " ").Trim();

    // The days' write-ups are Claude's own text, but titles can carry what the user typed: redacted like everything sent.
    private static string Clean(string s, int max)
    {
        var t = Tidy(JournalPrompt.Redact(s));
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}
