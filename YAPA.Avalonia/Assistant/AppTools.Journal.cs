using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using YAPA.Avalonia.Journal;
using static YAPA.Avalonia.Assistant.JsonSchema;

namespace YAPA.Avalonia.Assistant;

/// <summary>The day journal, for questions about what was done ("what did I do this morning?").</summary>
public sealed partial class AppTools
{
    private IEnumerable<AssistantTool> JournalTools() => _extras.Journal is not { } journal ? [] :
    [
        new AssistantTool
        {
            Name = "get_journal",
            Description = "What the user did during one day, pomodoro by pomodoro and session by session (a session is a run of pomodoros between long breaks), " +
                          "and the time at the computer off the timer (breaks, and before, between and after sessions), " +
                          "put together from their Claude Code conversations (titles and what they typed), git commits, browser history and the windows in front. " +
                          "Each pomodoro has a written title and detail when the day has been written up, otherwise a title from the evidence; the evidence is always there. " +
                          "Use it for anything about what was done: a session, the morning, a break, yesterday, a standup, whether a day went anywhere, what a video or page was, when something was last worked on. " +
                          "Nothing is on record while Tor Browser (or another app the user listed) was open, nor from apps they skip. Read-only.",
            Schema = Object(("date", Str("'today', 'yesterday' or YYYY-MM-DD; today when left out."), false)),
            Activity = _ => "Reading your day",
            OffUiThread = true,
            Run = a => new ToolResult(DescribeDay(journal, Day(journal, a.String("date")))),
        },

        new AssistantTool
        {
            Name = "name_pomodoro",
            Description = "Gives a pomodoro in the journal a name of the user's own (\"the 2 pm one was the AWS lab\"), which replaces the written title; an empty title gives it back to the write-up. Find ids with get_journal.",
            Schema = Object(
                ("pomodoro_id", Int("The pomodoro's id, from get_journal."), true),
                ("title", Str("A few words; empty to clear."), true)),
            Mutates = true,
            Activity = _ => "Naming that pomodoro",
            Run = a =>
            {
                var id = (int)(a.Number("pomodoro_id") ?? throw new ToolException("\"pomodoro_id\" is required."));
                var record = _history.Find(id) ?? throw new ToolException($"There is no pomodoro {id}.");
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(record.DateTime, DateTimeKind.Utc), TimeZoneInfo.Local));
                var title = (a.String("title") ?? "").Trim();
                var before = journal.UserTitle(id);
                journal.SetTitle(day, id, title.Length > 0 ? title : null);
                _journal.Record(() => journal.SetTitle(day, id, before));
                var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(record.DateTime, DateTimeKind.Utc), TimeZoneInfo.Local);
                var done = title.Length > 0 ? $"Named the pomodoro that ended at {when:HH:mm} \"{title}\"" : $"Let the write-up name the pomodoro that ended at {when:HH:mm}";
                return new ToolResult(new JsonObject { ["done"] = done }, done);
            },
        },
    ];

    private static DateOnly Day(JournalService journal, string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "today" => journal.Today,
        "yesterday" => journal.Today.AddDays(-1),
        var t when DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => throw new ToolException("\"date\" is 'today', 'yesterday' or YYYY-MM-DD."),
    };

    private static JsonObject DescribeDay(JournalService journal, DateOnly date)
    {
        var day = journal.Load(date);
        string T(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        // The answer can come from the evidence now; the write-up catches up for the Day window.
        if (day.Stale) journal.ScheduleWrite(date, TimeSpan.Zero);

        var result = new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture),
            ["pomodoros"] = day.Count,
            ["focus_minutes"] = day.Minutes,
            // Without the windows recorded, time off the timer is moments that left a trace, not a length.
            ["minutes_off_the_timer"] = day.WindowsRecorded ? day.OffTimerMinutes : null,
            ["written_up"] = !day.Stale,
        };
        if (day.Summary is { } s)
        {
            result["headline"] = s.Headline;
            result["summary"] = s.Summary;
        }
        if (day.Entries.Count == 0)
        {
            result["note"] = "Nothing on record that day.";
            return result;
        }
        result["sessions"] = new JsonArray(day.Sessions.Select(session => (JsonNode)new JsonObject
        {
            ["number"] = session.Number,
            ["from"] = T(session.StartUtc),
            ["to"] = T(session.EndUtc),
            // Counted here: a model reading a list miscounts it.
            ["pomodoro_count"] = day.Entries.Count(e => e.Session == session.Number && !e.InProgress && !e.OffTimer),
            ["focus_minutes"] = session.Blocks.Where(b => b.PomodoroId != 0).Sum(b => b.Minutes),
            ["title"] = day.SessionSummary(session.Number)?.Title,
            ["summary"] = day.SessionSummary(session.Number)?.Summary,
            ["pomodoros"] = new JsonArray(day.Entries.Where(e => e.Session == session.Number && !e.OffTimer).Select(e => (JsonNode)Describe(e, T)).ToArray()),
            ["breaks"] = new JsonArray(day.Entries.Where(e => e.Session == session.Number && e.OffTimer).Select(e => (JsonNode)Describe(e, T)).ToArray()),
        }).ToArray());
        // Time at the computer before, between and after sessions; ids are negative.
        result["off_the_timer"] = new JsonArray(day.Entries.Where(e => e.Session == 0).Select(e => (JsonNode)Describe(e, T)).ToArray());
        result["sources"] = string.Join(", ", day.Sources);
        return result;
    }

    private static JsonObject Describe(JournalEntry e, Func<DateTime, string> time)
    {
        var o = new JsonObject
        {
            ["id"] = e.Block.PomodoroId,
            ["from"] = time(e.Block.StartUtc),
            ["to"] = time(e.Block.EndUtc),
            ["minutes"] = e.Block.Minutes,
            ["title"] = e.Title.Length > 0 ? e.Title : "(nothing on record)",
        };
        if (e.InProgress) o["in_progress"] = true;
        if (e.UserTitle != null) o["named_by_user"] = true;
        if (e.Detail.Length > 0) o["detail"] = e.Detail;
        if (e.Projects.Count > 0) o["projects"] = new JsonArray(e.Projects.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());

        var evidence = new JsonObject();
        if (e.Evidence.Sessions.Count > 0)
            evidence["claude_code"] = new JsonArray(e.Evidence.Sessions.Select(s => (JsonNode)new JsonObject
            {
                ["conversation"] = s.Title,
                ["project"] = s.Project,
                ["minutes"] = s.Minutes,
                ["typed"] = new JsonArray(s.Prompts.Take(4).Select(p => (JsonNode)JsonValue.Create(Clip(p.Text, 180))!).ToArray()),
            }).ToArray());
        if (e.Evidence.Commits.Count > 0)
            evidence["commits"] = new JsonArray(e.Evidence.Commits.Select(c => (JsonNode)JsonValue.Create($"{c.Repo}: {c.Subject}")!).ToArray());
        if (e.Evidence.Sites.Count > 0)
            evidence["pages"] = new JsonArray(e.Evidence.Sites.Take(5).Select(s => (JsonNode)JsonValue.Create($"{s.Site}: {string.Join(" | ", s.Titles)}")!).ToArray());
        if (e.Evidence.Apps.Count > 0)
            evidence["windows"] = new JsonArray(e.Evidence.Apps.Take(4).Select(a => (JsonNode)JsonValue.Create($"{a.App} {a.Minutes} min")!).ToArray());
        if (evidence.Count > 0) o["evidence"] = evidence;
        return o;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
