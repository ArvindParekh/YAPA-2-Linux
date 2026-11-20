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
    private IEnumerable<AssistantTool> JournalTools() => _extras.Journal is not { } journal ? [] : ReadingTools(journal).Concat(PrivacyTools(journal));

    private IEnumerable<AssistantTool> ReadingTools(JournalService journal) =>
    [
        new AssistantTool
        {
            Name = "get_journal",
            Description = "What the user did during one day, pomodoro by pomodoro and session by session (a session is a run of pomodoros between long breaks), " +
                          "and the time at the computer off the timer (breaks, and before, between and after sessions), " +
                          "put together from their Claude Code conversations (titles and what they typed), git commits, browser history and the windows in front. " +
                          "Each pomodoro has a written title and detail when the day has been written up, otherwise a title from the evidence; the evidence is always there. " +
                          "Use it for anything about what was done: a session, the morning, a break, yesterday, a standup, whether a day went anywhere, what a video or page was, when something was last worked on. " +
                          "Nothing is on record while Tor Browser (or another app the user listed) was open, while an adult site was in front, for time the user " +
                          "went off the record or forgot, nor from apps they skip; a pomodoro in such time shows as off the record. Read-only.",
            Schema = Object(("date", Str("'today', 'yesterday' or YYYY-MM-DD; today when left out."), false)),
            Activity = _ => "Reading your day",
            OffUiThread = true,
            Run = a => new ToolResult(DescribeDay(journal, Day(journal, a.String("date")))),
        },

        new AssistantTool
        {
            Name = "get_week",
            Description = "The user's week at a glance, for 'how did this week go?', 'what did I do last week?', a weekly review or status update: " +
                          "pomodoros and focus per day with each day's headline, focus time by project across the week, and the week's own write-up " +
                          "when it has been written (in Your day's week view). For what happened inside a day, use get_journal on that day. Read-only.",
            Schema = Object(("date", Str("Any day in the week: 'today', 'yesterday', 'last week' or YYYY-MM-DD; this week when left out."), false)),
            Activity = _ => "Reading your week",
            OffUiThread = true,
            Run = a => new ToolResult(DescribeWeek(journal, a.String("date"))),
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

    private IEnumerable<AssistantTool> PrivacyTools(JournalService journal)
    {
        var offRecord = journal.OffRecord;
        string Hm(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        string State(DateTime? until) => until is not { } u ? "on the record"
            : u >= OffTheRecord.Indefinitely ? "off the record until turned back on" : $"off the record until {Hm(u)}";

        yield return new AssistantTool
        {
            Name = "go_off_the_record",
            Description = "Takes the user off the record for a while: the day journal records nothing (no windows), and nothing any source finds in that time " +
                          "(browser history, Claude Code, git) is ever used or sent. Pomodoros still run and count. For 'go off the record', 'stop watching me for an hour', " +
                          "'don't log the next 30 minutes'. Leave minutes out for 'until I turn it back on'. Called while already off the record, it moves the end. " +
                          "The timer shows a crossed-out eye while it lasts.",
            Schema = Object(("minutes", Int("How long, in minutes; leave out for until the user turns it back on.", 1, 24 * 60), false)),
            Mutates = true,
            Activity = _ => "Going off the record",
            Run = a =>
            {
                var before = offRecord.Until;
                var minutes = a.Number("minutes") is { } m ? (int)m : (int?)null;
                offRecord.Start(minutes is { } n ? TimeSpan.FromMinutes(n) : null);
                _journal.Record(() => offRecord.Restore(before));
                var done = minutes is { } x ? $"Off the record until {Hm(offRecord.Until!.Value)}" : "Off the record until you turn it back on";
                return new ToolResult(new JsonObject { ["done"] = done, ["now"] = State(offRecord.Until) }, done);
            },
        };

        yield return new AssistantTool
        {
            Name = "back_on_the_record",
            Description = "Ends time off the record now, so the day journal records again. The time already off the record stays off it.",
            Mutates = true,
            Activity = _ => "Going back on the record",
            Run = _ =>
            {
                var before = offRecord.Until;
                if (before == null) return new ToolResult(new JsonObject { ["done"] = "Already on the record" });
                offRecord.Stop();
                _journal.Record(() => offRecord.Restore(before));
                return new ToolResult(new JsonObject { ["done"] = "Back on the record" }, "Back on the record");
            },
        };

        yield return new AssistantTool
        {
            Name = "forget_time",
            Description = "Forgets a stretch of time for good, for 'forget what I did after 11 pm', 'delete that video from my day'. Deletes the windows recorded in it, " +
                          "every write-up of it and the names of pomodoros wholly in it, and keeps whatever any source has from it (browser history, Claude Code, " +
                          "commits) out of the journal from now on; the browser's and Claude Code's own records are left as they are. Pomodoros in it stay and still " +
                          "count, shown as off the record. Ask YAPA's saved conversations since its start are deleted too, and this conversation ends once you've answered " +
                          "(it has seen that time), unless keep_conversations. Cannot be undone. Times are the user's local time; look them up with get_journal when " +
                          "they name a thing rather than a time, and forget exactly that entry's from–to. Your answer says only which times were forgotten, never what was in them.",
            Schema = Object(
                ("from", Str("Start: 'HH:MM' on date, or 'YYYY-MM-DD HH:MM'."), true),
                ("to", Str("End: 'HH:MM' on date, 'YYYY-MM-DD HH:MM', or 'now'."), true),
                ("date", Str("'today', 'yesterday' or YYYY-MM-DD, for times given as HH:MM; today when left out."), false),
                ("keep_conversations", Bool("Keep Ask YAPA's conversations (only when the user says so)."), false)),
            Mutates = true,
            Irreversible = true,
            OffUiThread = true,
            Activity = _ => "Forgetting that time",
            Run = a =>
            {
                var date = Day(journal, a.String("date"));
                var from = LocalTime(a.String("from"), date, "from");
                var toText = a.String("to")?.Trim().ToLowerInvariant();
                var now = _clock.UtcNow;
                var to = toText == "now" ? now : LocalTime(toText, date, "to");
                if (to <= from) throw new ToolException("\"to\" must be after \"from\". For a stretch past midnight, give both as 'YYYY-MM-DD HH:MM'.");
                if (from >= now) throw new ToolException("That time hasn't happened yet; to keep the future off the record, use go_off_the_record.");
                if (to - from > TimeSpan.FromDays(31)) throw new ToolException("At most a month at a time.");
                var outcome = journal.Forget(from, to, conversations: a.Bool("keep_conversations") != true);
                var start = TimeZoneInfo.ConvertTimeFromUtc(outcome.FromUtc, TimeZoneInfo.Local);
                var end = TimeZoneInfo.ConvertTimeFromUtc(outcome.ToUtc, TimeZoneInfo.Local);
                var done = start.Date == end.Date
                    ? $"Forgot {start:HH:mm}–{end:HH:mm}"
                    : $"Forgot {start.ToString("ddd HH:mm", CultureInfo.InvariantCulture)} – {end.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
                return new ToolResult(new JsonObject
                {
                    ["done"] = done,
                    ["entries_reached"] = outcome.Entries,
                    ["conversations_deleted"] = outcome.Conversations,
                    ["note"] = "Say which times are gone; nothing about what was in them.",
                }, done);
            },
        };
    }

    /// <summary>"HH:MM" on <paramref name="date"/>, or "YYYY-MM-DD HH:MM", in local time, as UTC.</summary>
    private static DateTime LocalTime(string? text, DateOnly date, string name)
    {
        var t = text?.Trim() ?? "";
        DateTime local;
        if (TimeOnly.TryParseExact(t, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            local = date.ToDateTime(time);
        else if (!DateTime.TryParseExact(t, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            throw new ToolException($"\"{name}\" is 'HH:MM' or 'YYYY-MM-DD HH:MM'.");
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
    }

    private static DateOnly Day(JournalService journal, string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "today" => journal.Today,
        "yesterday" => journal.Today.AddDays(-1),
        var t when DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => throw new ToolException("\"date\" is 'today', 'yesterday' or YYYY-MM-DD."),
    };

    private static JsonObject DescribeWeek(JournalService journal, string? text)
    {
        var day = text?.Trim().ToLowerInvariant() == "last week" ? journal.Today.AddDays(-7) : Day(journal, text);
        var week = journal.LoadWeek(JournalService.WeekStart(day));
        var result = new JsonObject
        {
            ["week"] = $"{week.Start.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture)} to {week.Start.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ["pomodoros"] = week.Pomodoros,
            ["focus_minutes"] = week.FocusMinutes,
            ["days_with_pomodoros"] = week.ActiveDays,
        };
        if (week.Summary is { } s)
        {
            result["headline"] = s.Headline;
            result["summary"] = s.Summary;
            result["written_up"] = !week.Stale;
        }
        result["days"] = new JsonArray(week.Days.Select(d => (JsonNode)new JsonObject
        {
            ["date"] = d.Date.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture),
            ["pomodoros"] = d.Count,
            ["focus_minutes"] = d.Minutes,
            ["headline"] = d.Summary?.Headline is { Length: > 0 } h ? h : null,
        }).ToArray());
        result["by_project"] = new JsonArray(week.Projects().Select(p => (JsonNode)new JsonObject { ["project"] = p.Name, ["focus_minutes"] = p.Minutes }).ToArray());
        return result;
    }

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
            ["title"] = e.OffRecord ? "(off the record)" : e.Title.Length > 0 ? e.Title : "(nothing on record)",
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
