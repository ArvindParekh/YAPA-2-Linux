using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;
using static YAPA.Avalonia.Assistant.JsonSchema;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// Everything the assistant can see and do in YAPA, as MCP tools. Each tool goes through
/// the same engine, settings and history the rest of the app uses, so the timer, tray and
/// open windows follow at once, and every change lands in the <see cref="ChangeJournal"/>.
/// </summary>
public sealed partial class AppTools
{
    private const int MaxLogged = 24;

    private readonly IPomodoroEngine _engine;
    private readonly PomodoroEngineSettings _profiles;
    private readonly GeneralSettings _general;
    private readonly IPomodoroRepository _history;
    private readonly AppNavigator _navigator;
    private readonly SettingsCatalog _catalog;
    private readonly ChangeJournal _journal;
    private readonly AmbienceSettings _ambience;
    private readonly AmbienceEffects _ambienceEffects;
    private readonly IClock _clock;

    public AppTools(IPomodoroEngine engine, PomodoroEngineSettings profiles, GeneralSettings general, IPomodoroRepository history,
        AppNavigator navigator, SettingsCatalog catalog, ChangeJournal journal, AmbienceSettings ambience,
        AmbienceEffects ambienceEffects, IClock clock, Extras extras)
    {
        _extras = extras;
        _engine = engine;
        _profiles = profiles;
        _general = general;
        _history = history;
        _navigator = navigator;
        _catalog = catalog;
        _journal = journal;
        _ambience = ambience;
        _ambienceEffects = ambienceEffects;
        _clock = clock;
    }

    private DateTime Now => _clock.UtcNow.ToLocalTime();

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>The live state, compact enough to send with every message.</summary>
    public JsonObject State()
    {
        var now = Now;
        var e = _engine;
        var phase = e.Phase;
        var underway = phase == PomodoroPhase.Pause ? e.PausedPhase : phase;
        var inBreak = underway == PomodoroPhase.Break || phase == PomodoroPhase.BreakEnded;
        var done = _history.CompletedToday();
        var goal = _general.DailyGoal;
        var outlook = GoalOutlook.Compute(e, _profiles, goal, done, _clock.UtcNow);

        var timer = new JsonObject
        {
            ["state"] = phase switch
            {
                PomodoroPhase.Work => "pomodoro_running",
                PomodoroPhase.Break => "break_running",
                PomodoroPhase.Pause => inBreak ? "break_paused" : "pomodoro_paused",
                PomodoroPhase.WorkEnded => "pomodoro_ended (waiting for the break to be started)",
                PomodoroPhase.BreakEnded => "break_ended (waiting for the next pomodoro to be started)",
                _ => "idle (nothing running; the next pomodoro is ready to start)",
            },
        };
        if (phase != PomodoroPhase.NotStarted)
        {
            timer["interval"] = inBreak ? (e.IsLongBreak ? "long_break" : "short_break") : "pomodoro";
            timer["length_minutes"] = Minutes(e.CurrentIntervalLength);
            if (e.ExtraTime != 0) timer["changed_by_minutes"] = Minutes(e.ExtraTime);
        }
        if (e.CanAdjustInterval)
        {
            timer["elapsed"] = Clock(e.Elapsed);
            timer["remaining"] = Clock(e.Remaining);
            if (e.IsRunning) timer["ends_at"] = Time(now.AddSeconds(e.Remaining));
        }
        if (e.Overrun is { } overrun) timer["waiting_for"] = Clock(overrun) + " since it ended (the clock counts this up)";
        if (e.OverrunStopped) timer["waiting_quietly"] = true;
        timer["start_would"] = phase switch
        {
            PomodoroPhase.Work or PomodoroPhase.Break => "nothing (already running)",
            PomodoroPhase.Pause => inBreak ? "resume the break" : "resume the pomodoro",
            PomodoroPhase.WorkEnded => e.IsLongBreak ? "start the long break" : "start the short break",
            _ => $"start pomodoro {NextIndex()} of the cycle",
        };

        var cycleLength = Math.Max(1, _profiles.PomodorosBeforeLongBreak);
        var state = new JsonObject
        {
            ["now"] = now.ToString("yyyy-MM-dd HH:mm (dddd)", CultureInfo.InvariantCulture),
            ["timer"] = timer,
            ["cycle"] = new JsonObject
            {
                ["pomodoro"] = e.Index,
                ["of"] = cycleLength,
                ["cycle_today"] = e.Cycle,
                ["break_after_this_pomodoro"] = e.IsLongBreak ? "long" : "short",
            },
            ["today"] = new JsonObject
            {
                ["done"] = done,
                ["daily_goal"] = goal > 0 ? goal : null,
                ["goal_reached_by"] = outlook.Finish is { } f ? Time(f, now) : null,
                ["goal_reached_by_without_long_breaks"] = outlook.FinishWithoutLongBreaks is { } w ? Time(w, now) : null,
            },
            ["profile"] = ProfileJson(_profiles.ActiveProfile, CurrentProfile()),
            ["ambience"] = new JsonObject
            {
                ["sound"] = _catalog.Get("ambience.sound"),
                ["switched_on"] = _ambience.Enabled,
                ["ticking"] = _ambience.Ticking,
                ["playing_now"] = _ambienceEffects.IsLive,
            },
            ["timer_window_visible"] = _navigator.IsTimerShowing,
        };
        var others = _profiles.Profiles.Keys.Where(k => k != _profiles.ActiveProfile).ToArray();
        if (others.Length > 0) state["other_profiles"] = new JsonArray(others.Select(o => (JsonNode)o).ToArray());
        AddExtrasTo(state, now);
        return state;
    }

    /// <summary>The pomodoro that starts next from idle or after a break.</summary>
    private int NextIndex()
    {
        if (_engine.Phase != PomodoroPhase.BreakEnded) return _engine.Index;
        return _engine.IsLongBreak ? 1 : _engine.Index + 1;
    }

    // ── Tools ────────────────────────────────────────────────────────────────

    public IEnumerable<AssistantTool> All() => Core().Concat(More());

    private IEnumerable<AssistantTool> Core() =>
    [
        new AssistantTool
        {
            Name = "get_state",
            Description = "The timer and today's progress right now: what is running, time left, position in the cycle, today's count and goal forecast, the active profile and the background sound. The same snapshot comes with every user message; call this to see the effect of an action or when time has passed.",
            Activity = _ => "Checking the timer",
            Run = _ => new ToolResult(State()),
        },

        new AssistantTool
        {
            Name = "control_timer",
            Description = """
                Operates the timer like its buttons do.
                - start: starts whatever is next in sequence: the next pomodoro when idle or after a break; the break (short or long, as scheduled) right after a pomodoro ended; resumes when paused. Optional already_elapsed_minutes for a pomodoro the user actually began earlier ("I started 10 minutes ago").
                - pause / resume: the running interval.
                - stop: abandons a running or paused pomodoro (it is NOT recorded; the same pomodoro can be retried), or ends a break early. The timer goes idle.
                - skip_break: skips the break that is due or under way and starts the next pomodoro immediately.
                - take_long_break: takes the long break now (from idle, right after a pomodoro, or during a short break, which then becomes long). Afterwards the cycle starts over at pomodoro 1.
                - dismiss_overrun: after an interval ended, stops the "+mm:ss" count-up of time since it ended, and keeps waiting quietly.
                - reset_cycle: back to pomodoro 1 of the cycle, idle. Nothing recorded is touched.
                """,
            Schema = Object(
                ("action", Str("What to do.", "start", "pause", "resume", "stop", "skip_break", "take_long_break", "dismiss_overrun", "reset_cycle"), true),
                ("already_elapsed_minutes", Number("With start, for a pomodoro: how many minutes ago the user really began it."), false),
                ("confirmed", Bool("With stop or reset_cycle: the user agreed to drop the pomodoro under way. Required once it has a minute or more in it."), false)),
            Mutates = true,
            TouchesEngine = true,
            Activity = a => a.String("action") switch
            {
                "pause" => "Pausing",
                "resume" => "Resuming",
                "stop" => "Stopping",
                "skip_break" => "Skipping the break",
                "take_long_break" => "Starting the long break",
                "dismiss_overrun" => "Quieting the timer",
                "reset_cycle" => "Resetting the cycle",
                _ => "Starting",
            },
            Run = ControlTimer,
        },

        new AssistantTool
        {
            Name = "adjust_interval",
            Description = "Changes the interval that is running or paused, without stopping it. Give exactly one of: add_minutes (negative to shorten; \"5 more minutes of break\", \"cut this pomodoro by 10\"), remaining_minutes (\"make it 10 minutes from now\"), elapsed_minutes (\"I've been at it 20 minutes already\"). Shortened to or below the time already run, the interval ends now. The change lasts for this interval only; use update_settings to change the lengths for good.",
            Schema = Object(
                ("add_minutes", Number("Minutes to add; negative to take off."), false),
                ("remaining_minutes", Number("Minutes the interval should have left from now."), false),
                ("elapsed_minutes", Number("Minutes the interval should count as already done."), false)),
            Mutates = true,
            TouchesEngine = true,
            Activity = a => a.Number("add_minutes") is { } m ? (m >= 0 ? $"Adding {FormatMinutes(m)}" : $"Taking off {FormatMinutes(-m)}") : "Adjusting the timer",
            Run = AdjustInterval,
        },

        new AssistantTool
        {
            Name = "log_pomodoros",
            Description = """
                Records pomodoros the user did away from the timer (a walk, a meeting, forgot to start it), each with its break, exactly like the "Mark a pomodoro done" menu item: they count in today's total and the statistics, and the cycle moves on past them, so the timer then waits at the pomodoro after them (after the cycle's last one, the long break is taken as done and a new cycle begins).
                Needs the timer idle or waiting between intervals when advance_cycle is true (the default). With a pomodoro or break running or paused, either stop it first if the user wants that, or pass advance_cycle false to only add them to the history.
                Without ended_at they are placed back to back ending now. Give ended_at (local times) when the user says when they happened.
                """,
            Schema = Object(
                ("count", Int("How many pomodoros.", 1, MaxLogged), true),
                ("ended_at", Array("When each one ended, local time, 'YYYY-MM-DD HH:MM' or 'HH:MM' for today; oldest first; one per pomodoro.", Str("A local time.")), false),
                ("minutes", Int("Length of each, in minutes. Defaults to the active profile's pomodoro length.", 1, 240), false),
                ("advance_cycle", Bool("Move the timer's cycle on past them (default true). False only adds them to the history."), false),
                ("profile", Str("Profile to record them under; defaults to the active one."), false)),
            Mutates = true,
            TouchesEngine = true,
            Activity = a => a.Int("count") is { } n && n != 1 ? $"Logging {n} pomodoros" : "Logging a pomodoro",
            Run = LogPomodoros,
        },

        new AssistantTool
        {
            Name = "set_cycle_position",
            Description = "Moves the timer to another pomodoro of the cycle WITHOUT recording anything (\"skip ahead to the 4th\", \"I'm actually on my third\"), and optionally sets which cycle of the day it is. The timer waits there, idle. Needs the timer idle or waiting between intervals. To also count the skipped ones as done, use log_pomodoros instead.",
            Schema = Object(
                ("pomodoro", Int("Position in the cycle, starting at 1.", 1, 24), true),
                ("cycle", Int("Which cycle of the day it should be, starting at 1.", 1, 50), false)),
            Mutates = true,
            TouchesEngine = true,
            Activity = a => $"Moving to pomodoro {a.Int("pomodoro")}",
            Run = SetCyclePosition,
        },

        new AssistantTool
        {
            Name = "plan_schedule",
            Description = "Works out the rest of the day on the timer's own rules (lengths, cycle position, which break is long, the interval under way), assuming each interval starts as soon as the previous one ends. Use it for anything about the future: \"how many can I finish if I stop at 6?\", \"when would 4 more be done?\", \"if I start at 3, when do I finish 6?\". Give until, pomodoros, or both. Do the arithmetic with this tool, never by hand.",
            Schema = Object(
                ("until", Str("Plan up to this local time, 'HH:MM' (today) or 'YYYY-MM-DD HH:MM'."), false),
                ("pomodoros", Int("Plan this many pomodoros, the one under way included.", 1, 48), false),
                ("start_at", Str("If nothing is under way: when the first pomodoro would start (local time). Default now."), false),
                ("long_breaks", Bool("Take long breaks as scheduled (default true); false treats them as short ones."), false)),
            Activity = _ => "Working out the schedule",
            Run = PlanSchedule,
        },

        new AssistantTool
        {
            Name = "get_statistics",
            Description = "Summary statistics, as the Statistics window shows them: today, this week, last week, this month and all time (pomodoros and focus minutes), current and best streak, best day, average per active day, the last 14 days, and how many focus minutes fell in each hour of the day over a recent window (the peak is when the user focuses most). Weeks start on Monday.",
            Schema = Object(
                ("profile", Str("Only this profile; default all."), false),
                ("hour_window_days", Int("Days the hour-of-day profile covers, ending today (default 28).", 1, 3650), false)),
            Activity = _ => "Reading your statistics",
            OffUiThread = true,
            Run = Statistics,
        },

        new AssistantTool
        {
            Name = "query_history",
            Description = "Runs a read-only SQLite query over the complete pomodoro history and the timer's event log, for any question the statistics don't answer directly (weekdays, ranges, profiles, specific days, breaks, abandoned pomodoros, when the day started). Returns up to 200 rows; aggregate in SQL. " + HistoryQuery.Schema,
            Schema = Object(("sql", Str("One SELECT statement (WITH … SELECT is fine)."), true)),
            Activity = _ => "Looking through your history",
            OffUiThread = true,
            Run = a => new ToolResult(HistoryQuery.Run(a.RequiredString("sql"), _history.All(), TimeZoneInfo.Local, _extras.Events.All())),
        },

        new AssistantTool
        {
            Name = "edit_history",
            Description = "Adds, changes or deletes recorded pomodoros, without touching the timer (use log_pomodoros for pomodoros done off the timer, which also moves the cycle on). Find ids with query_history first. Times are local.",
            Schema = Object(
                ("add", Array("Records to add.", Object(
                    ("ended_at", Str("When it ended: 'YYYY-MM-DD HH:MM' or 'HH:MM' for today."), true),
                    ("minutes", Int("Its length; default the active profile's pomodoro length.", 1, 240), false),
                    ("profile", Str("Default the active profile."), false))), false),
                ("update", Array("Records to change; omitted fields stay.", Object(
                    ("id", Int("The record's id."), true),
                    ("ended_at", Str("New end time."), false),
                    ("minutes", Int("New length.", 0, 240), false),
                    ("profile", Str("New profile."), false))), false),
                ("delete", Array("Ids of records to delete.", Int("A record id.")), false)),
            Mutates = true,
            Activity = _ => "Editing your history",
            Run = EditHistory,
        },

        new AssistantTool
        {
            Name = "get_settings",
            Description = "Every setting with its current value, what it does and what it accepts. Optional prefix narrows it (timer., goal., sound., ambience., appearance., window., general., assistant.).",
            Schema = Object(("prefix", Str("Only settings whose key starts with this."), false)),
            Activity = _ => "Reading your settings",
            Run = a => new ToolResult(new JsonObject { ["settings"] = _catalog.Describe(a.String("prefix")) }),
        },

        new AssistantTool
        {
            Name = "update_settings",
            Description = "Changes settings; they apply at once and are saved. Keys as in get_settings (e.g. timer.work_minutes, goal.daily_goal, ambience.sound, appearance.text_color). All changes are checked first; if one is invalid, none is made. timer.* settings belong to the active profile.",
            Schema = Object(("changes", Map("Setting key → new value, e.g. {\"timer.work_minutes\": 45, \"ambience.sound\": \"soft_rain\"}."), true)),
            Mutates = true,
            Activity = _ => "Changing your settings",
            Run = UpdateSettings,
        },

        new AssistantTool
        {
            Name = "manage_profiles",
            Description = "Profiles are named sets of timer lengths (pomodoro, breaks, cycle length, auto-start). create (copying copy_from or the active profile, then applying the given lengths), switch (make it the active one; the running interval takes the new lengths at once), rename, delete (not the last one), update (change the lengths of a profile, the active one by default).",
            Schema = Object(
                ("action", Str("What to do.", "create", "switch", "rename", "delete", "update"), true),
                ("name", Str("The profile to act on (for create: the new one's name). Defaults to the active profile for update."), false),
                ("new_name", Str("For rename."), false),
                ("copy_from", Str("For create: the profile to start from; default the active one."), false),
                ("switch_to", Bool("For create: make it active too."), false),
                ("work_minutes", Int("Pomodoro length.", 1, 240), false),
                ("short_break_minutes", Int("Short break length.", 1, 120), false),
                ("long_break_minutes", Int("Long break length.", 1, 240), false),
                ("pomodoros_per_cycle", Int("Pomodoros before the long break.", 1, 24), false),
                ("auto_start_breaks", Bool("Start breaks by themselves."), false),
                ("auto_start_pomodoros", Bool("Start pomodoros by themselves after a break."), false)),
            Mutates = true,
            TouchesEngine = true, // switching profile changes the interval under way
            Activity = a => a.String("action") switch
            {
                "switch" => $"Switching to {a.String("name")}",
                "create" => $"Creating {a.String("name")}",
                _ => "Updating profiles",
            },
            Run = ManageProfiles,
        },

        new AssistantTool
        {
            Name = "open_window",
            Description = "Shows part of the app: the timer (show_timer / hide_timer), the Settings window at a page, or the Statistics window. Opening a window closes this assistant panel, so only do it when asked.",
            Schema = Object(
                ("window", Str("Which.", "show_timer", "hide_timer", "settings", "statistics"), true),
                ("page", Str("For settings.", "timer", "appearance", "sounds", "general", "about"), false)),
            Activity = a => a.String("window") switch
            {
                "statistics" => "Opening statistics",
                "settings" => "Opening settings",
                "hide_timer" => "Hiding the timer",
                _ => "Showing the timer",
            },
            Run = OpenWindow,
        },

        new AssistantTool
        {
            Name = "undo_last_change",
            Description = "Undoes everything the assistant changed in its most recent request that changed something (timer, history and settings together), as the Undo button does. Each call goes one request further back.",
            Activity = _ => "Undoing",
            Run = _ =>
            {
                var undone = _journal.Undo() ?? throw new ToolException("There is nothing left to undo.");
                _navigator.RefreshSettings();
                return new ToolResult(new JsonObject { ["undid"] = undone, ["state"] = State() }, "Undid: " + undone);
            },
        },
    ];

    // ── Timer ────────────────────────────────────────────────────────────────

    private ToolResult ControlTimer(ToolArgs a)
    {
        var e = _engine;
        var phase = e.Phase;
        var pausedBreak = phase == PomodoroPhase.Pause && e.PausedPhase == PomodoroPhase.Break;
        var workUnderWay = phase == PomodoroPhase.Work || (phase == PomodoroPhase.Pause && !pausedBreak);
        string done;

        // Stopping (or resetting) drops the pomodoro unrecorded; a model reading "skip" or "start
        // over" too eagerly must not throw away work without the user saying so.
        void GuardWork()
        {
            if (workUnderWay && e.Elapsed >= 60 && a.Bool("confirmed") != true)
                throw new ToolException(
                    $"The pomodoro under way is {FormatMinutes(Math.Floor(e.Elapsed / 60.0))} in, and this would drop it unrecorded. " +
                    "Ask the user first: count it as done, drop it, or keep it going. Call again with confirmed true only if they want it dropped.");
        }

        switch (a.RequiredString("action"))
        {
            case "start":
                if (e.IsRunning) throw new ToolException($"The {IntervalName()} is already running.");
                var wasPaused = phase == PomodoroPhase.Pause;
                e.Start();
                if (a.Number("already_elapsed_minutes") is { } ago && e.Phase == PomodoroPhase.Work && !wasPaused)
                    e.SetElapsed((int)Math.Round(ago * 60));
                done = wasPaused ? $"Resumed the {IntervalName()}"
                    : e.Phase == PomodoroPhase.Work ? $"Started pomodoro {e.Index}" + (e.Elapsed >= 60 ? $", {Clock(e.Elapsed)} in" : "")
                    : $"Started the {IntervalName()}";
                break;

            case "pause":
                if (!e.IsRunning) throw new ToolException("Nothing is running to pause.");
                e.Pause();
                done = $"Paused the {IntervalName()}";
                break;

            case "resume":
                if (phase != PomodoroPhase.Pause) throw new ToolException("Nothing is paused.");
                e.Start();
                done = $"Resumed the {IntervalName()}";
                break;

            case "stop":
                if (phase == PomodoroPhase.NotStarted) throw new ToolException("The timer is already idle.");
                GuardWork();
                e.Stop();
                done = workUnderWay ? "Dropped the pomodoro (not recorded)" : "Ended the break";
                break;

            case "skip_break":
                if (!(phase is PomodoroPhase.WorkEnded or PomodoroPhase.Break or PomodoroPhase.BreakEnded || pausedBreak))
                    throw new ToolException("There is no break to skip right now.");
                e.Skip();
                done = $"Skipped the break; pomodoro {e.Index} started";
                break;

            case "take_long_break":
                if (!e.CanStartLongBreak) throw new ToolException("A long break can't start during a pomodoro, or the break is already long.");
                e.StartLongBreak();
                done = "Started the long break";
                break;

            case "dismiss_overrun":
                if (e.Overrun == null) throw new ToolException("Nothing is counting up: no interval is waiting to be followed.");
                e.StopOverrun();
                done = "Stopped the count-up";
                break;

            case "reset_cycle":
                if (phase == PomodoroPhase.NotStarted && e.Index == 1) throw new ToolException("The cycle is already at its first pomodoro.");
                GuardWork();
                var dropping = workUnderWay && e.Elapsed > 0;
                e.Reset();
                done = dropping ? "Dropped the pomodoro in progress; back to pomodoro 1" : "Reset the cycle to pomodoro 1";
                break;

            default:
                throw new ToolException("Unknown action.");
        }

        return new ToolResult(new JsonObject { ["done"] = done, ["state"] = State() }, done);
    }

    /// <summary>"pomodoro", "break" or "long break": the interval running, paused or just ended.</summary>
    private string IntervalName()
    {
        var e = _engine;
        var inBreak = e.Phase is PomodoroPhase.Break or PomodoroPhase.BreakEnded
            || (e.Phase == PomodoroPhase.Pause && e.PausedPhase == PomodoroPhase.Break);
        return inBreak ? (e.IsLongBreak ? "long break" : "break") : "pomodoro";
    }

    private ToolResult AdjustInterval(ToolArgs a)
    {
        var e = _engine;
        if (!e.CanAdjustInterval)
            throw new ToolException("No pomodoro or break is running or paused, so there is nothing to adjust. Start one first if that's what the user wants.");

        var given = new[] { "add_minutes", "remaining_minutes", "elapsed_minutes" }.Where(a.Has).ToList();
        if (given.Count != 1) throw new ToolException("Give exactly one of add_minutes, remaining_minutes, elapsed_minutes.");

        var name = IntervalName();
        string done;
        switch (given[0])
        {
            case "add_minutes":
                var add = a.Number("add_minutes")!.Value;
                e.ExtendInterval((int)Math.Round(add * 60));
                done = add >= 0 ? $"Added {FormatMinutes(add)} to the {name}" : $"Took {FormatMinutes(-add)} off the {name}";
                break;
            case "remaining_minutes":
                var remaining = Math.Max(0, a.Number("remaining_minutes")!.Value);
                e.ExtendInterval((int)Math.Round(e.Elapsed + remaining * 60) - e.CurrentIntervalLength);
                done = $"The {name} now has {FormatMinutes(remaining)} left";
                break;
            default:
                var elapsed = Math.Max(0, a.Number("elapsed_minutes")!.Value);
                e.SetElapsed((int)Math.Round(elapsed * 60));
                done = $"Set the {name} to {FormatMinutes(elapsed)} in";
                break;
        }

        if (e.IsRunning && e.Remaining > 0) done += $" (ends at {Time(Now.AddSeconds(e.Remaining))})";
        return new ToolResult(new JsonObject { ["done"] = done, ["state"] = State() }, done);
    }

    private ToolResult LogPomodoros(ToolArgs a)
    {
        var count = a.Int("count") ?? 1;
        if (count < 1 || count > MaxLogged) throw new ToolException($"count must be 1 to {MaxLogged}.");
        var minutes = a.Int("minutes") ?? Math.Max(1, _engine.WorkTime / 60);
        var advance = a.Bool("advance_cycle") ?? true;
        var profile = a.String("profile") is { Length: > 0 } p ? p : _profiles.ActiveProfile;
        var now = Now;

        var times = a.Array("ended_at")?.Select(t => ParseLocal(t?.ToString() ?? "", now, "ended_at")).ToList();
        if (times != null)
        {
            if (times.Count != count) throw new ToolException($"ended_at has {times.Count} times for {count} pomodoros; give one per pomodoro.");
            if (times.Any(t => t > now.AddMinutes(1))) throw new ToolException("ended_at can't be in the future.");
            times.Sort();
        }
        else
        {
            // Back to back, the last ending now: each one and its short break before it.
            var step = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Math.Max(1, _profiles.BreakTime));
            times = Enumerable.Range(0, count).Select(i => now - step * (count - 1 - i)).ToList();
        }

        if (advance && !_engine.CanMarkDone)
        {
            throw new ToolException(
                $"The {IntervalName()} is {(_engine.IsRunning ? "running" : "paused")}. Logging with advance_cycle moves the cycle on, which needs the timer idle or between intervals. " +
                "Either stop it first (control_timer stop) if the user wants that, or call again with advance_cycle false to only add them to the history.");
        }

        var rows = new List<PomodoroEntity>();
        if (advance)
        {
            // The engine records each one through the usual path (the dashboard writes the row);
            // catch those rows to give them their real times.
            void OnChanged(HistoryChange c) { if (c.Kind == HistoryChangeKind.Added) rows.Add(c.After); }
            _history.Changed += OnChanged;
            try
            {
                for (var i = 0; i < count; i++) _engine.MarkDone();
            }
            finally
            {
                _history.Changed -= OnChanged;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = _history.Find(rows[i].Id);
                if (row == null) continue;
                row.DateTime = ToUtc(times[i]);
                row.DurationMin = minutes;
                row.ProfileName = profile;
                _history.Update(row);
            }
        }
        else
        {
            foreach (var t in times)
                _history.Add(new PomodoroEntity { Count = 1, DateTime = ToUtc(t), DurationMin = minutes, ProfileName = profile });
        }

        var doneToday = _history.CompletedToday();
        var what = count == 1 ? "a pomodoro" : $"{count} pomodoros";
        var done = $"Logged {what} ({doneToday}{(_general.DailyGoal > 0 ? $" of {_general.DailyGoal}" : "")} today)";
        if (advance) done += $"; next up: pomodoro {_engine.Index} of {Math.Max(1, _profiles.PomodorosBeforeLongBreak)}";
        return new ToolResult(new JsonObject { ["done"] = done, ["state"] = State() }, done);
    }

    private ToolResult SetCyclePosition(ToolArgs a)
    {
        var position = a.Int("pomodoro") ?? throw new ToolException("\"pomodoro\" is required.");
        var cycleLength = Math.Max(1, _profiles.PomodorosBeforeLongBreak);
        if (position < 1 || position > cycleLength)
            throw new ToolException($"The cycle has {cycleLength} pomodoros, so the position must be 1 to {cycleLength}.");
        if (!_engine.CanMarkDone)
            throw new ToolException($"The {IntervalName()} is {(_engine.IsRunning ? "running" : "paused")}; moving in the cycle needs the timer idle or between intervals. Stop it first if the user wants that.");

        var cycle = a.Int("cycle");
        _engine.MoveTo(position, cycle);
        var done = $"Moved to pomodoro {position} of {cycleLength}" + (cycle is { } c ? $", cycle {c}" : "");
        return new ToolResult(new JsonObject { ["done"] = done, ["state"] = State() }, done);
    }

    // ── Planning and statistics ──────────────────────────────────────────────

    private ToolResult PlanSchedule(ToolArgs a)
    {
        var now = Now;
        DateTime? until = a.String("until") is { Length: > 0 } u ? ParseLocal(u, now, "until") : null;
        if (until is { } end && end <= now) throw new ToolException($"until ({Time(end, now)}) has already passed; give a later time, with a date if it's another day.");
        var pomodoros = a.Int("pomodoros");
        if (until == null && pomodoros == null) throw new ToolException("Give until, pomodoros, or both.");
        DateTime? startAt = a.String("start_at") is { Length: > 0 } s ? ParseLocal(s, now, "start_at") : null;
        var longBreaks = a.Bool("long_breaks") ?? true;
        var doneToday = _history.CompletedToday();

        var plan = SchedulePlan.Build(_engine, _profiles, now, until, pomodoros, startAt, longBreaks, doneToday);
        var finished = plan.Where(p => p.Kind == PlannedKind.Pomodoro && !p.CutOff).ToList();
        var cut = plan.FirstOrDefault(p => p.CutOff);

        var result = new JsonObject
        {
            ["assumes"] = "each interval started the moment the previous one ends" + (_engine.Phase == PomodoroPhase.Pause ? "; the paused interval resumed now" : ""),
            ["done_today_so_far"] = doneToday,
            ["pomodoros_completed_in_plan"] = finished.Count,
            ["done_today_at_end_of_plan"] = doneToday + finished.Count,
            ["last_pomodoro_ends_at"] = finished.LastOrDefault() is { } last ? Time(last.End, now) : null,
            ["intervals"] = new JsonArray(plan.Select(p => (JsonNode)new JsonObject
            {
                ["kind"] = p.Kind switch { PlannedKind.Pomodoro => "pomodoro", PlannedKind.LongBreak => "long_break", _ => "short_break" },
                ["number_today"] = p.Number,
                ["start"] = Time(p.Start, now),
                ["end"] = Time(p.End, now),
                ["under_way"] = p.InProgress ? true : null,
                ["not_finished_by_until"] = p.CutOff ? true : null,
            }).ToArray()),
        };
        if (cut != null && cut.Kind == PlannedKind.Pomodoro && until is { } stop)
            result["note"] = $"Pomodoro {cut.Number} would start at {Time(cut.Start, now)} and end at {Time(cut.End, now)}, after {Time(stop, now)}.";
        return new ToolResult(result);
    }

    private ToolResult Statistics(ToolArgs a)
    {
        var profile = a.String("profile") is { Length: > 0 } p ? p : null;
        var days = a.Int("hour_window_days") ?? 28;
        var report = PomodoroStatistics.Compute(_history.All(), new StatisticsQuery(TimeZoneInfo.Local, DateTime.UtcNow)
        {
            Profile = profile,
            HourOfDayDays = Math.Clamp(days, 1, 3650),
        });

        JsonObject Period(PeriodTotal t) => new()
        {
            ["from"] = t.First.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = t.Last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pomodoros"] = t.Count,
            ["focus_minutes"] = t.FocusMinutes,
            ["active_days"] = t.ActiveDays,
        };
        JsonObject? StreakJson(Streak s) => s.IsEmpty ? null : new JsonObject
        {
            ["days"] = s.Length,
            ["from"] = s.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = s.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        var weekStart = LocalCalendar.StartOfWeek(report.Today, DayOfWeek.Monday);
        var hours = report.HourOfDay.Minutes;
        var result = new JsonObject
        {
            ["profile"] = profile ?? "all",
            ["today"] = Period(report.TodayTotal),
            ["this_week"] = Period(report.ThisWeek),
            ["last_week"] = Period(report.Daily.Sum(weekStart.AddDays(-7), weekStart.AddDays(-1))),
            ["this_month"] = Period(report.ThisMonth),
            ["all_time"] = Period(report.AllTime),
            ["current_streak"] = StreakJson(report.CurrentStreak),
            ["best_streak"] = StreakJson(report.BestStreak),
            ["best_day"] = report.BestDay is { } best ? new JsonObject
            {
                ["date"] = best.Date.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture),
                ["pomodoros"] = best.Count,
                ["focus_minutes"] = best.FocusMinutes,
            } : null,
            ["average_per_active_day"] = new JsonObject
            {
                ["pomodoros"] = Math.Round(report.AverageCountPerActiveDay, 1),
                ["focus_minutes"] = Math.Round(report.AverageMinutesPerActiveDay),
            },
            ["last_14_days"] = new JsonArray(report.Daily.Range(report.Today.AddDays(-13), report.Today)
                .Select(d => (JsonNode)$"{d.Date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture)}: {d.Count}").ToArray()),
            ["focus_by_hour"] = new JsonObject
            {
                ["window_days"] = report.HourOfDay.Days,
                ["peak_hour"] = report.HourOfDay.PeakHour is { } peak ? $"{peak:00}:00–{(peak + 1) % 24:00}:00, the hour of the day with the most focus" : null,
                // Minutes of focus that fell within each hour of the day (a pomodoro across the hour counts in both).
                ["focus_minutes_in_each_hour"] = new JsonObject(Enumerable.Range(0, 24)
                    .Where(h => hours[h] > 0)
                    .Select(h => new KeyValuePair<string, JsonNode?>($"{h:00}:00", (int)Math.Round(hours[h])))),
            },
            ["profiles"] = new JsonArray(report.Profiles.Select(n => (JsonNode)n).ToArray()),
        };
        return new ToolResult(result);
    }

    // ── History ──────────────────────────────────────────────────────────────

    private ToolResult EditHistory(ToolArgs a)
    {
        var now = Now;
        var defaultMinutes = Math.Max(1, _engine.WorkTime / 60);
        var adds = a.Array("add") ?? [];
        var updates = a.Array("update") ?? [];
        var deletes = a.Array("delete") ?? [];
        if (adds.Count + updates.Count + deletes.Count == 0) throw new ToolException("Nothing to do: give add, update or delete.");

        // Everything is checked before anything is written.
        var toAdd = adds.Select(n => n as JsonObject ?? throw new ToolException("Each item of add must be an object.")).Select(o =>
        {
            var args = new ToolArgs(o);
            var at = ParseLocal(args.RequiredString("ended_at"), now, "ended_at");
            if (at > now.AddMinutes(1)) throw new ToolException("A record can't end in the future.");
            return new PomodoroEntity
            {
                Count = 1,
                DateTime = ToUtc(at),
                DurationMin = args.Int("minutes") ?? defaultMinutes,
                ProfileName = args.String("profile") is { Length: > 0 } p ? p : _profiles.ActiveProfile,
            };
        }).ToList();

        var toUpdate = updates.Select(n => n as JsonObject ?? throw new ToolException("Each item of update must be an object.")).Select(o =>
        {
            var args = new ToolArgs(o);
            var id = args.Int("id") ?? throw new ToolException("Each update needs an id.");
            var row = _history.Find(id) ?? throw new ToolException($"There is no record {id}.");
            if (args.String("ended_at") is { Length: > 0 } t) row.DateTime = ToUtc(ParseLocal(t, now, "ended_at"));
            if (args.Int("minutes") is { } m) row.DurationMin = m;
            if (args.String("profile") is { Length: > 0 } p) row.ProfileName = p;
            return row;
        }).ToList();

        var toDelete = deletes.Select(n => n is JsonValue v && v.TryGetValue<int>(out var id) ? id
            : int.TryParse(n?.ToString(), out var parsed) ? parsed : throw new ToolException("delete takes record ids.")).Distinct().ToList();
        foreach (var id in toDelete)
            if (_history.Find(id) == null) throw new ToolException($"There is no record {id}.");

        foreach (var row in toAdd) _history.Add(row);
        foreach (var row in toUpdate) _history.Update(row);
        foreach (var id in toDelete) _history.Delete(id);

        var parts = new List<string>();
        if (toAdd.Count > 0) parts.Add($"added {Plural(toAdd.Count, "record")}");
        if (toUpdate.Count > 0) parts.Add($"changed {Plural(toUpdate.Count, "record")}");
        if (toDelete.Count > 0) parts.Add($"deleted {Plural(toDelete.Count, "record")}");
        var done = char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
        return new ToolResult(new JsonObject
        {
            ["done"] = done,
            ["added_ids"] = new JsonArray(toAdd.Select(r => (JsonNode)r.Id).ToArray()),
            ["done_today"] = _history.CompletedToday(),
        }, done);
    }

    // ── Settings and profiles ────────────────────────────────────────────────

    private ToolResult UpdateSettings(ToolArgs a)
    {
        var changes = a.Object("changes") ?? throw new ToolException("\"changes\" is required: an object of setting key → value.");
        var applied = _catalog.Apply(changes, _journal);
        _navigator.RefreshSettings();
        if (applied.Count == 0) return new ToolResult(new JsonObject { ["done"] = "Nothing changed: those were already the values." });

        var parts = applied.Select(c => _catalog.Describe(c!["key"]!.GetValue<string>(), c["to"])).ToList();
        var done = "Set " + JoinWords(parts);
        return new ToolResult(new JsonObject { ["changed"] = applied, ["state"] = State() }, done);
    }

    /// <summary>"a, b and c".</summary>
    private static string JoinWords(IReadOnlyList<string> parts)
        => parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private ToolResult ManageProfiles(ToolArgs a)
    {
        var profiles = _profiles.Profiles;
        var active = _profiles.ActiveProfile;
        var action = a.RequiredString("action");
        var name = a.String("name")?.Trim();
        string done;

        // Undo restores the whole set and the choice of active profile.
        var before = profiles.ToDictionary(p => p.Key, p => p.Value.Clone());
        var activeBefore = active;
        void Remember() => _journal.Record(() =>
        {
            _profiles.Profiles = before.ToDictionary(p => p.Key, p => p.Value.Clone());
            _profiles.ActiveProfile = activeBefore;
        });

        string Existing(string? n)
        {
            if (string.IsNullOrEmpty(n)) throw new ToolException("Say which profile (name).");
            return profiles.Keys.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"There is no profile \"{n}\". Profiles: {string.Join(", ", profiles.Keys)}.");
        }

        void ApplyLengths(PomodoroProfile p)
        {
            if (a.Int("work_minutes") is { } w) p.WorkTime = Math.Clamp(w, 1, 240) * 60;
            if (a.Int("short_break_minutes") is { } sb) p.BreakTime = Math.Clamp(sb, 1, 120) * 60;
            if (a.Int("long_break_minutes") is { } lb) p.LongBreakTime = Math.Clamp(lb, 1, 240) * 60;
            if (a.Int("pomodoros_per_cycle") is { } c) p.PomodorosBeforeLongBreak = Math.Clamp(c, 1, 24);
            if (a.Bool("auto_start_breaks") is { } ab) p.AutoStartBreak = ab;
            if (a.Bool("auto_start_pomodoros") is { } aw) p.AutoStartWork = aw;
        }

        switch (action)
        {
            case "create":
                if (string.IsNullOrEmpty(name)) throw new ToolException("Give the new profile a name.");
                if (profiles.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException($"A profile called \"{name}\" already exists.");
                var source = a.String("copy_from") is { Length: > 0 } from ? Existing(from) : active;
                var created = profiles[source].Clone();
                ApplyLengths(created);
                Remember();
                profiles[name] = created;
                _profiles.Profiles = profiles;
                if (a.Bool("switch_to") == true) _profiles.ActiveProfile = name;
                done = $"Created the {name} profile" + (a.Bool("switch_to") == true ? " and switched to it" : "");
                break;

            case "switch":
                var target = Existing(name);
                if (target == active) throw new ToolException($"{target} is already the active profile.");
                Remember();
                _profiles.ActiveProfile = target;
                done = $"Switched to the {target} profile";
                break;

            case "rename":
                var old = Existing(name);
                var newName = a.String("new_name")?.Trim();
                if (string.IsNullOrEmpty(newName)) throw new ToolException("Give new_name.");
                if (profiles.Keys.Any(k => k != old && string.Equals(k, newName, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException($"A profile called \"{newName}\" already exists.");
                Remember();
                var renamed = profiles[old];
                profiles.Remove(old);
                profiles[newName] = renamed;
                _profiles.Profiles = profiles;
                if (old == active) _profiles.ActiveProfile = newName;
                done = $"Renamed {old} to {newName}";
                break;

            case "delete":
                var doomed = Existing(name);
                if (profiles.Count == 1) throw new ToolException("That is the only profile; it can't be deleted.");
                Remember();
                profiles.Remove(doomed);
                _profiles.Profiles = profiles;
                if (doomed == active) _profiles.ActiveProfile = profiles.Keys.First();
                done = $"Deleted the {doomed} profile" + (doomed == active ? $"; {_profiles.ActiveProfile} is active now" : "");
                break;

            case "update":
                var edited = string.IsNullOrEmpty(name) ? active : Existing(name);
                var updated = profiles[edited].Clone();
                ApplyLengths(updated);
                Remember();
                profiles[edited] = updated;
                _profiles.Profiles = profiles;
                done = $"Updated the {edited} profile";
                break;

            default:
                throw new ToolException("Unknown action.");
        }

        _navigator.RefreshSettings();
        return new ToolResult(new JsonObject
        {
            ["done"] = done,
            ["profiles"] = new JsonArray(_profiles.Profiles.Select(p => (JsonNode)ProfileJson(p.Key, p.Value)).ToArray()),
            ["active"] = _profiles.ActiveProfile,
        }, done);
    }

    private PomodoroProfile CurrentProfile()
        => _profiles.Profiles.TryGetValue(_profiles.ActiveProfile, out var p) ? p : PomodoroProfile.Default();

    private static JsonObject ProfileJson(string name, PomodoroProfile p) => new()
    {
        ["name"] = name,
        ["work_minutes"] = p.WorkTime / 60,
        ["short_break_minutes"] = p.BreakTime / 60,
        ["long_break_minutes"] = p.LongBreakTime / 60,
        ["pomodoros_per_cycle"] = p.PomodorosBeforeLongBreak,
        ["auto_start_breaks"] = p.AutoStartBreak,
        ["auto_start_pomodoros"] = p.AutoStartWork,
    };

    // ── Windows ──────────────────────────────────────────────────────────────

    private ToolResult OpenWindow(ToolArgs a)
    {
        string done;
        switch (a.RequiredString("window"))
        {
            case "statistics":
                _navigator.ShowStatistics();
                done = "Opened Statistics";
                break;
            case "settings":
                var page = a.String("page") switch
                {
                    "appearance" => SettingsPage.Appearance,
                    "sounds" => SettingsPage.Sounds,
                    "general" => SettingsPage.General,
                    "about" => SettingsPage.About,
                    _ => SettingsPage.Timer,
                };
                _navigator.ShowSettings(page);
                done = $"Opened Settings ({page})";
                break;
            case "hide_timer":
                _navigator.HideTimer();
                done = "Hid the timer";
                break;
            default:
                _navigator.ShowTimer();
                done = "Showed the timer";
                break;
        }
        return new ToolResult(new JsonObject { ["done"] = done }, done);
    }

    // ── Formatting and parsing ───────────────────────────────────────────────

    private static double Minutes(int seconds) => Math.Round(seconds / 60.0, 1);

    private static string Clock(int seconds)
        => seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}" : $"{seconds / 60:00}:{seconds % 60:00}";

    /// <summary>24-hour "HH:mm", with the date when it isn't today: the model rewrites it in the user's own format.</summary>
    private static string Time(DateTime local, DateTime? now = null)
    {
        var day = now?.Date ?? DateTime.Now.Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == day ? time
            : local.Date == day.AddDays(1) ? time + " tomorrow"
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatMinutes(double minutes)
    {
        var rounded = Math.Round(minutes, 1);
        return rounded == 1 ? "1 minute" : $"{rounded.ToString("0.#", CultureInfo.InvariantCulture)} minutes";
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd h:mm tt", "yyyy-MM-dd h tt",
    ];

    private static readonly string[] TimeFormats =
    [
        "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss", "h:mm tt", "h:mmtt", "h tt", "htt", "h:mm t", "HH.mm",
    ];

    /// <summary>A local time from the model: a time today, or a date and time.</summary>
    internal static DateTime ParseLocal(string text, DateTime now, string field)
    {
        var s = text.Trim();
        var culture = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(s, DateTimeFormats, culture, DateTimeStyles.AllowWhiteSpaces, out var full))
            return full;
        if (DateTime.TryParseExact(s, TimeFormats, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var time))
            return now.Date + time.TimeOfDay;
        // An ISO time with an offset or Z: an instant, shown in local time.
        if (s.Length > 10 && DateTimeOffset.TryParse(s, culture, DateTimeStyles.AllowWhiteSpaces, out var instant)
            && (s.EndsWith('Z') || s.Contains('+') || s.LastIndexOf('-') > 9))
            return instant.LocalDateTime;
        throw new ToolException($"{field} \"{text}\" isn't a time I can read; use 'HH:MM' for today or 'YYYY-MM-DD HH:MM'.");
    }
}
