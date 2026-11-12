using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Library;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Windows;
using YAPA.Shared.Common;
using static YAPA.Avalonia.Assistant.JsonSchema;

namespace YAPA.Avalonia.Assistant;

public sealed partial class AppTools
{
    /// <summary>What the tools beyond the timer need: later actions, memory, the event log, and every settings object for resets.</summary>
    public sealed record Extras(Scheduler Scheduler, AssistantMemory Memory, TimerEventLog Events, AvaloniaYapaThemeSettings Look,
        AvaloniaThemeSettings Themes, SoundSettings Sounds, AvaloniaMusicPlayerSettings Music);

    private readonly Extras _extras;

    private void AddExtrasTo(JsonObject state, DateTime now)
    {
        if (_extras.Scheduler.Items.Count > 0)
            state["scheduled"] = new JsonArray(_extras.Scheduler.Items
                .Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["what"] = i.Describe(now) }).ToArray());
        if (_extras.Memory.Notes.Count > 0)
            state["remembered"] = new JsonArray(_extras.Memory.Notes
                .Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["note"] = n.Text }).ToArray());
    }

    private IEnumerable<AssistantTool> More() =>
    [
        new AssistantTool
        {
            Name = "schedule",
            Description = """
                Does something later, once, even if this conversation has ended: at a time ("stop at 6", "start the next one at 14:30"), after a delay ("pause in 10 minutes"), or when the current interval ends ("when this pomodoro ends, take the long break"; "after the break, skip straight to work" is start with when break_ends). remind shows a desktop notification with the message ("remind me to stretch at 4"). The user gets a notification when it runs. Pending items are listed in the state snapshot as "scheduled".
                Actions: start (the next interval in sequence), pause, resume, stop, skip_break, take_long_break, remind.
                Give exactly one of at, in_minutes, when.
                """,
            Schema = Object(
                ("action", Str("What to do.", ScheduledAction.Actions), true),
                ("at", Str("Local time: 'HH:MM' (today, or tomorrow if already past) or 'YYYY-MM-DD HH:MM'."), false),
                ("in_minutes", Number("From now."), false),
                ("when", Str("On an event.", ScheduledAction.PomodoroEnds, ScheduledAction.BreakEnds), false),
                ("message", Str("For remind: what to say."), false)),
            Mutates = true,
            Activity = _ => "Scheduling it",
            Run = Schedule,
        },

        new AssistantTool
        {
            Name = "cancel_scheduled",
            Description = "Cancels something scheduled earlier, by its id from the state snapshot, or all of them.",
            Schema = Object(("id", Str("The item's id."), false), ("all", Bool("Cancel everything scheduled."), false)),
            Mutates = true,
            Activity = _ => "Cancelling it",
            Run = CancelScheduled,
        },

        new AssistantTool
        {
            Name = "move_timer",
            Description = "Moves the timer window to a spot on its screen (it is kept clear of the dock and top bar on its own).",
            Schema = Object(("spot", Str("Where.", TimerWindowBase.Spots), true)),
            Mutates = true,
            Activity = _ => "Moving the timer",
            Run = MoveTimer,
        },

        new AssistantTool
        {
            Name = "list_sounds",
            Description = "What can be played: the built-in cue sounds, the radio stations for music, and the audio files in the user's Sounds folder (usable for cues, music and the background recording). Use it before setting sound.cue.*, music.* or ambience.recording_file.",
            Activity = _ => "Looking at your sounds",
            Run = _ => new ToolResult(new JsonObject
            {
                ["built_in_cues"] = new JsonArray(SoundRef.BuiltIns.Select(b => (JsonNode)b.Serialize().Replace("builtin:", "")).ToArray()),
                ["radio_stations"] = new JsonArray(MediaLibrary.Radio.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Reference, ["name"] = r.Name, ["about"] = r.Description,
                }).ToArray()),
                ["sounds_folder"] = MediaLibrary.Folder,
                ["sounds_folder_files"] = new JsonArray(MediaLibrary.LibraryFiles().Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name, ["path"] = f.Location,
                }).ToArray()),
            }),
        },

        new AssistantTool
        {
            Name = "export_history",
            Description = "Writes the recorded pomodoros to a CSV file (local times), optionally for a date range, and says where it went. Default location: ~/Documents/YAPA history <date>.csv.",
            Schema = Object(
                ("from", Str("First local date, 'YYYY-MM-DD'."), false),
                ("to", Str("Last local date, 'YYYY-MM-DD'."), false),
                ("path", Str("Where to write it: an absolute path, or ~/…"), false)),
            Activity = _ => "Exporting your history",
            OffUiThread = true,
            Run = ExportHistory,
        },

        new AssistantTool
        {
            Name = "remember",
            Description = "Keeps a note for every future conversation: a preference, a rule of thumb, a fact about the user's routine (\"a walk counts one pomodoro an hour\", \"I like brutal honesty\"). Notes come back in the state snapshot as \"remembered\". Only when the user asks you to remember something, or clearly states a lasting preference.",
            Schema = Object(("note", Str("One short sentence, in the third person about the user."), true)),
            Mutates = true,
            Activity = _ => "Remembering that",
            Run = a =>
            {
                var note = _extras.Memory.Remember(a.RequiredString("note"));
                _journal.Record(() => _extras.Memory.Forget(note.Id));
                return new ToolResult(new JsonObject { ["remembered"] = note.Text, ["id"] = note.Id }, $"Remembered: {note.Text}");
            },
        },

        new AssistantTool
        {
            Name = "forget",
            Description = "Drops a remembered note, by its id from the state snapshot.",
            Schema = Object(("id", Str("The note's id."), true)),
            Mutates = true,
            Activity = _ => "Forgetting that",
            Run = a =>
            {
                var id = a.RequiredString("id");
                var note = _extras.Memory.Notes.FirstOrDefault(n => n.Id == id) ?? throw new ToolException($"There is no note {id}.");
                _extras.Memory.Forget(id);
                _journal.Record(() => _extras.Memory.Restore(note));
                return new ToolResult(new JsonObject { ["forgot"] = note.Text }, $"Forgot: {note.Text}");
            },
        },

        new AssistantTool
        {
            Name = "recall_conversations",
            Description = "Earlier conversations with the user (before the current one): what they asked, what you answered, what was changed. For \"what did I ask you yesterday\", \"like I said before\".",
            Schema = Object(("days", Int("How far back, in days (default 7).", 1, 365), false)),
            Activity = _ => "Looking back",
            OffUiThread = true,
            Run = a =>
            {
                var since = DateTime.UtcNow.AddDays(-(a.Int("days") ?? 7));
                var conversations = _extras.Memory.Conversations(since).Take(30);
                return new ToolResult(new JsonObject
                {
                    ["conversations"] = new JsonArray(conversations.Select(c => (JsonNode)new JsonObject
                    {
                        ["started"] = c.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        ["exchanges"] = new JsonArray(c.Exchanges.Select(x => (JsonNode)new JsonObject
                        {
                            ["asked"] = x.Question,
                            ["answered"] = x.Answer.Length > 400 ? x.Answer[..400] + "…" : x.Answer,
                            ["changed"] = x.Actions.Count > 0 ? string.Join("; ", x.Actions) : null,
                        }).ToArray()),
                    }).ToArray()),
                });
            },
        },

        new AssistantTool
        {
            Name = "reset_settings",
            Description = "Puts a section of the settings back to defaults, as the Settings window's Reset does: timer (the active profile's lengths, auto-start, counter), appearance, sounds (cues, volume, music, background sound), general (window, startup, notifications, goal), or all. Profiles and history are never touched.",
            Schema = Object(("section", Str("Which.", "timer", "appearance", "sounds", "general", "all"), true)),
            Mutates = true,
            Activity = a => $"Resetting {a.String("section")} settings",
            Run = ResetSettings,
        },

        new AssistantTool
        {
            Name = "read_app_log",
            Description = "The end of YAPA's own log, to work out why something didn't behave (no sound, a notification missing, the timer jumping). Read-only.",
            Schema = Object(
                ("lines", Int("How many lines from the end (default 80).", 1, 400), false),
                ("contains", Str("Only lines containing this, e.g. \"[sound]\", \"[timer]\", \"error\"."), false)),
            Activity = _ => "Reading the log",
            OffUiThread = true,
            Run = ReadLog,
        },

        new AssistantTool
        {
            Name = "quit_app",
            Description = "Quits YAPA a moment after your reply, saving the timer (it is restored, paused, at the next start). Only when the user asks to quit or close the app.",
            Activity = _ => "Quitting",
            Run = _ =>
            {
                // After the reply has had time to show.
                DispatcherTimer.RunOnce(() => _navigator.Quit(), TimeSpan.FromSeconds(2.5));
                return new ToolResult(new JsonObject { ["done"] = "YAPA quits in a couple of seconds." }, "Quitting YAPA");
            },
        },
    ];

    // ── Later ────────────────────────────────────────────────────────────────

    private ToolResult Schedule(ToolArgs a)
    {
        var action = a.RequiredString("action");
        if (!ScheduledAction.Actions.Contains(action)) throw new ToolException("Unknown action.");
        var given = new[] { "at", "in_minutes", "when" }.Where(a.Has).ToList();
        if (given.Count != 1) throw new ToolException("Give exactly one of at, in_minutes, when.");
        var message = a.String("message");
        if (action == "remind" && string.IsNullOrWhiteSpace(message)) throw new ToolException("A reminder needs a message.");

        var now = Now;
        DateTime? atUtc = null;
        string? when = null;
        switch (given[0])
        {
            case "at":
                var at = ParseLocal(a.RequiredString("at"), now, "at");
                // A bare time already gone today means tomorrow: "start at 8" said at night.
                if (at <= now && !a.RequiredString("at").Contains('-')) at = at.AddDays(1);
                if (at <= now) throw new ToolException("That time has already passed.");
                atUtc = ToUtc(at);
                break;
            case "in_minutes":
                var minutes = a.Number("in_minutes")!.Value;
                if (minutes <= 0 || minutes > 24 * 60) throw new ToolException("in_minutes must be more than 0 and at most a day.");
                atUtc = _clock.UtcNow.AddMinutes(minutes);
                break;
            default:
                when = a.RequiredString("when");
                if (when is not (ScheduledAction.PomodoroEnds or ScheduledAction.BreakEnds)) throw new ToolException("Unknown event.");
                break;
        }

        var item = _extras.Scheduler.Add(action, atUtc, when, message);
        _journal.Record(() => _extras.Scheduler.Remove(item.Id));
        var done = "Will " + char.ToLowerInvariant(item.Describe(now)[0]) + item.Describe(now)[1..];
        return new ToolResult(new JsonObject { ["scheduled"] = item.Describe(now), ["id"] = item.Id }, done);
    }

    private ToolResult CancelScheduled(ToolArgs a)
    {
        var now = Now;
        var targets = a.Bool("all") == true
            ? _extras.Scheduler.Items.ToList()
            : [_extras.Scheduler.Items.FirstOrDefault(i => i.Id == a.String("id"))
               ?? throw new ToolException("No scheduled item has that id; the state snapshot lists them.")];
        if (targets.Count == 0) throw new ToolException("Nothing is scheduled.");
        foreach (var item in targets)
        {
            _extras.Scheduler.Remove(item.Id);
            _journal.Record(() => _extras.Scheduler.Restore(item));
        }
        var done = targets.Count == 1 ? "Cancelled: " + targets[0].Describe(now) : $"Cancelled {targets.Count} scheduled actions";
        return new ToolResult(new JsonObject { ["done"] = done }, done);
    }

    // ── Window ───────────────────────────────────────────────────────────────

    private ToolResult MoveTimer(ToolArgs a)
    {
        var spot = a.RequiredString("spot");
        if (_navigator.TimerWindow is not { IsVisible: true } timer) throw new ToolException("The timer is hidden; show it first.");
        var before = timer.Position;
        if (!timer.MoveToSpot(spot)) throw new ToolException("Unknown spot.");
        _journal.Record(() => timer.Position = before);
        var done = $"Moved the timer to the {spot.Replace('_', ' ')}";
        return new ToolResult(new JsonObject { ["done"] = done }, done);
    }

    // ── Files ────────────────────────────────────────────────────────────────

    private ToolResult ExportHistory(ToolArgs a)
    {
        DateOnly? Date(string name) => a.String(name) is { Length: > 0 } s
            ? DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
              : throw new ToolException($"{name} must be YYYY-MM-DD.")
            : null;
        var from = Date("from");
        var to = Date("to");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = a.String("path") is { Length: > 0 } p
            ? (p.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, p[2..]) : p)
            : Path.Combine(home, "Documents", $"YAPA history {DateTime.Now:yyyy-MM-dd}.csv");
        if (!Path.IsPathRooted(path)) throw new ToolException("Give an absolute path, or one starting with ~/.");
        if (Directory.Exists(path)) path = Path.Combine(path, $"YAPA history {DateTime.Now:yyyy-MM-dd}.csv");

        var rows = _history.All()
            .Select(r => (Row: r, Local: DateTime.SpecifyKind(r.DateTime, DateTimeKind.Utc).ToLocalTime()))
            .Where(r => (from == null || DateOnly.FromDateTime(r.Local) >= from) && (to == null || DateOnly.FromDateTime(r.Local) <= to))
            .ToList();

        var csv = new StringBuilder("date,started,ended,minutes,count,profile\n");
        foreach (var (row, local) in rows)
        {
            var started = local.AddMinutes(-Math.Max(0, row.DurationMin));
            var profile = (row.ProfileName ?? "").Replace("\"", "\"\"");
            csv.Append(CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd},{started:HH:mm},{local:HH:mm},{row.DurationMin},{row.Count},\"{profile}\"\n");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, csv.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"Couldn't write {path}: {e.Message}");
        }
        var done = $"Exported {rows.Count} pomodoros to {path.Replace(home, "~")}";
        return new ToolResult(new JsonObject { ["path"] = path, ["rows"] = rows.Count }, done);
    }

    private static ToolResult ReadLog(ToolArgs a)
    {
        var count = Math.Clamp(a.Int("lines") ?? 80, 1, 400);
        var filter = a.String("contains");
        try
        {
            using var stream = new FileStream(Diagnostics.Logging.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n')
                .Where(l => l.Length > 0 && (string.IsNullOrEmpty(filter) || l.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(count)
                .Select(l => l.Length > 300 ? l[..300] + "…" : l);
            return new ToolResult(new JsonObject { ["log"] = string.Join("\n", lines) });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"The log can't be read: {e.Message}");
        }
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private ToolResult ResetSettings(ToolArgs a)
    {
        var section = a.RequiredString("section");
        // Undo puts back every value as it was; the catalog covers everything a reset touches.
        var before = new JsonObject();
        foreach (var entry in _catalog.Entries.Where(e => e.Key is not ("general.launch_at_login" or "general.start_hidden")))
            before[entry.Key] = entry.Get()?.DeepClone();

        var all = section == "all";
        if (all || section == "timer") _profiles.ResetToDefaults();
        if (all || section == "appearance")
        {
            _extras.Look.ResetToDefaults();
            _extras.Themes.ResetToDefaults();
        }
        if (all || section == "sounds")
        {
            _extras.Sounds.ResetToDefaults();
            _extras.Music.ResetToDefaults();
            _ambience.ResetToDefaults();
            _profiles.Volume = 0.5;
            _profiles.DisableSoundNotifications = false;
        }
        if (all || section == "general")
        {
            _general.ResetToDefaults();
            _extras.Look.MinimizeToTray = true; // shown on the General page
        }
        if (!all && section is not ("timer" or "appearance" or "sounds" or "general")) throw new ToolException("Unknown section.");

        _journal.Record(() => _catalog.Restore(before));
        _navigator.RefreshSettings();
        var done = all ? "Reset every setting to its default" : $"Reset the {section} settings to defaults";
        return new ToolResult(new JsonObject { ["done"] = done, ["state"] = State() }, done);
    }
}
