using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Media;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <param name="Type">bool, integer, percent, choice or color, as the model sees it.</param>
/// <param name="Constraint">The range or the choices, in words for the model.</param>
public sealed record SettingEntry(string Key, string Description, string Type, string? Constraint, Func<JsonNode?> Get, Action<JsonNode?> Set);

/// <summary>
/// Every user setting under one stable, descriptive name, read and written through the same
/// typed settings objects the settings window uses, so a change applies at once and
/// everything listening (timer, ambience, open windows) follows.
/// </summary>
public sealed class SettingsCatalog
{
    private readonly Dictionary<string, SettingEntry> _entries;

    public SettingsCatalog(PomodoroEngineSettings engine, GeneralSettings general, AvaloniaYapaThemeSettings look,
        AvaloniaThemeSettings themes, AmbienceSettings ambience, SoundSettings sounds, AssistantSettings assistant,
        AvaloniaMusicPlayerSettings music, TimerTexts texts)
    {
        var entries = new List<SettingEntry>
        {
            // ── Timer (the active profile) ───────────────────────────────────────
            Int("timer.work_minutes", "Length of a pomodoro, in the active profile.", 1, 240,
                () => engine.WorkTime / 60, v => engine.WorkTime = v * 60),
            Int("timer.short_break_minutes", "Length of a short break, in the active profile.", 1, 120,
                () => engine.BreakTime / 60, v => engine.BreakTime = v * 60),
            Int("timer.long_break_minutes", "Length of the long break that ends each cycle, in the active profile.", 1, 240,
                () => engine.LongBreakTime / 60, v => engine.LongBreakTime = v * 60),
            Int("timer.pomodoros_per_cycle", "Pomodoros in a cycle; the long break comes after the last one.", 1, 24,
                () => engine.PomodorosBeforeLongBreak, v => engine.PomodorosBeforeLongBreak = v),
            Bool("timer.auto_start_breaks", "Start the break by itself when a pomodoro ends.",
                () => engine.AutoStartBreak, v => engine.AutoStartBreak = v),
            Bool("timer.auto_start_pomodoros", "Start the next pomodoro by itself when a break ends.",
                () => engine.AutoStartWork, v => engine.AutoStartWork = v),
            Bool("timer.count_down", "The clock shows the time left (true) or the time elapsed (false).",
                () => engine.CountBackwards, v => engine.CountBackwards = v),
            Choice("timer.counter", "What the small number on the timer counts.",
                [("cycle_position", "position in the cycle, e.g. 2/4"), ("completed_today", "pomodoros done today against the daily goal, e.g. 5/12"), ("completed_since_launch", "pomodoros since YAPA started")],
                () => engine.Counter switch
                {
                    CounterEnum.CompletedToday => "completed_today",
                    CounterEnum.CompletedThisSession => "completed_since_launch",
                    _ => "cycle_position",
                },
                v => engine.Counter = v switch
                {
                    "completed_today" => CounterEnum.CompletedToday,
                    "completed_since_launch" => CounterEnum.CompletedThisSession,
                    _ => CounterEnum.PomodoroIndex,
                }),

            // ── Daily goal ──────────────────────────────────────────────────────
            Int("goal.daily_goal", "Pomodoros to aim for each day; 0 means no goal.", 0, 100,
                () => general.DailyGoal, v => general.DailyGoal = v),
            Bool("goal.show_forecast", "Show on the timer the time of day the goal will be reached (\"12 by 18:40\").",
                () => general.ShowGoalForecast, v => general.ShowGoalForecast = v),

            // ── Sounds ──────────────────────────────────────────────────────────
            Bool("sound.enabled", "Master switch for every sound YAPA plays: cues and background ambience.",
                () => !engine.DisableSoundNotifications, v => engine.DisableSoundNotifications = !v),
            Percent("sound.volume", "Volume of the cue sounds.", () => engine.Volume, v => engine.Volume = v),

            // ── Background ambience ─────────────────────────────────────────────
            Bool("ambience.enabled", "Quick on/off for the background sound, keeping the chosen sound.",
                () => ambience.Enabled, v => ambience.Enabled = v),
            Choice("ambience.sound", "The background sound during pomodoros.",
                [("none", "silence"), ("soft_rain", "light rain"), ("steady_rain", "rain"), ("downpour", "heavy rain"), ("brown_noise", "deep, even noise"), ("own_recording", "the user's own audio file, if one was chosen in Settings")],
                () => ambience.Sound switch
                {
                    AmbientSound.SoftRain => "soft_rain",
                    AmbientSound.SteadyRain => "steady_rain",
                    AmbientSound.Downpour => "downpour",
                    AmbientSound.BrownNoise => "brown_noise",
                    AmbientSound.File => "own_recording",
                    _ => "none",
                },
                v =>
                {
                    if (v == "own_recording" && string.IsNullOrEmpty(ambience.FilePath))
                        throw new ToolException("No recording has been chosen yet; that is done in Settings → Sounds.");
                    ambience.Sound = v switch
                    {
                        "soft_rain" => AmbientSound.SoftRain,
                        "steady_rain" => AmbientSound.SteadyRain,
                        "downpour" => AmbientSound.Downpour,
                        "brown_noise" => AmbientSound.BrownNoise,
                        "own_recording" => AmbientSound.File,
                        _ => AmbientSound.None,
                    };
                }),
            Percent("ambience.volume", "Volume of the background sound.", () => ambience.Volume, v => ambience.Volume = v),
            Bool("ambience.ticking", "A clock ticks during pomodoros.", () => ambience.Ticking, v => ambience.Ticking = v),
            Percent("ambience.tick_volume", "Volume of the ticking.", () => ambience.TickVolume, v => ambience.TickVolume = v),
            Bool("ambience.during_breaks", "Keep the background sound going through breaks (the ticking always stops).",
                () => ambience.DuringBreaks, v => ambience.DuringBreaks = v),
            Bool("ambience.wind_down", "Thin the sound out over the last minute of each interval.",
                () => ambience.WindDownBeforeEnd, v => ambience.WindDownBeforeEnd = v),

            // ── Appearance ──────────────────────────────────────────────────────
            Choice("appearance.theme", "The timer's look.",
                [("classic", "big digits with a column of text buttons"), ("motivational", "the timer with a motivational quote")],
                () => themes.ActiveTheme == AppNavigator.MotivationalTheme ? "motivational" : "classic",
                v => themes.ActiveTheme = v == "motivational" ? AppNavigator.MotivationalTheme : AppNavigator.ClassicTheme),
            Int("appearance.size", "Width of the timer in pixels (it keeps its proportions).", 120, 800,
                () => look.Width, v => look.Width = v),
            Percent("appearance.opacity", "How opaque the timer is (20–100%).", () => look.ClockOpacity, v => look.ClockOpacity = Math.Max(0.2, v)),
            Color("appearance.text_color", "Colour of the timer's digits and text.", () => look.TextColor, v => look.TextColor = v),
            Color("appearance.shadow_color", "Colour of the shadow behind the digits.", () => look.ShadowColor, v => look.ShadowColor = v),
            Percent("appearance.shadow_opacity", "Strength of the shadow behind the digits; 0 turns it off.",
                () => look.ShadowOpacity, v => look.ShadowOpacity = v),
            Bool("appearance.show_seconds", "Show seconds on the clock.", () => !look.HideSeconds, v => look.HideSeconds = !v),
            Bool("appearance.show_status", "Show the status line (\"Work\", \"Break\" …) above the clock.",
                () => look.ShowStatusText, v => look.ShowStatusText = v),
            Bool("appearance.show_progress", "Show the progress line under the clock.", () => look.ShowProgress, v => look.ShowProgress = v),
            Bool("appearance.controls_on_hover", "Show the buttons only while the pointer is over the timer.",
                () => look.HideButtons, v => look.HideButtons = v),
            Bool("appearance.flash_when_done", "Pulse the timer when an interval ends.",
                () => !look.DisableFlashingAnimation, v => look.DisableFlashingAnimation = !v),

            // ── Window ──────────────────────────────────────────────────────────
            Bool("window.always_on_top", "Keep the timer above other windows.", () => general.AlwaysOnTop, v => general.AlwaysOnTop = v),
            Bool("window.all_workspaces", "Show the timer on every workspace.", () => general.ShowOnAllWorkspaces, v => general.ShowOnAllWorkspaces = v),
            Bool("window.stay_on_show_desktop", "Keep the timer visible when Show Desktop hides everything else.",
                () => general.StayOnShowDesktop, v => general.StayOnShowDesktop = v),
            Bool("window.show_in_taskbar", "Show the timer in the dock and Alt+Tab.", () => general.ShowInTaskbar, v => general.ShowInTaskbar = v),
            Bool("window.avoid_shell_panels", "Keep the timer clear of the dock and top bar, where clicks don't reach it.",
                () => general.AvoidShellPanels, v => general.AvoidShellPanels = v),
            Bool("window.minimize_to_tray", "The timer's – button hides it to the tray instead of minimizing.",
                () => look.MinimizeToTray, v => look.MinimizeToTray = v),

            // ── General ─────────────────────────────────────────────────────────
            Bool("general.pause_when_locked", "Pause a running pomodoro when the screen locks.",
                () => general.PauseWhenLocked, v => general.PauseWhenLocked = v),
            Bool("general.desktop_notifications", "Show a desktop notification when an interval ends.",
                () => general.DesktopNotifications, v => general.DesktopNotifications = v),
            Bool("general.launch_at_login", "Start YAPA when the user logs in.",
                () => Autostart.IsEnabled, v => Autostart.SetEnabled(v, general.StartHidden)),
            Bool("general.start_hidden", "Start in the tray, without showing the timer.",
                () => general.StartHidden,
                v =>
                {
                    general.StartHidden = v;
                    if (Autostart.IsEnabled) Autostart.SetEnabled(true, v);
                }),

            // ── Assistant ───────────────────────────────────────────────────────
            Choice("assistant.model", "The Claude model behind this assistant; takes effect from the next conversation.",
                AssistantSettings.Models.Select(m => (m.Id, m.Detail)).ToArray(),
                () => assistant.Model, v => assistant.Model = v),
        };

        foreach (var cue in SoundSettings.All) entries.Add(Cue(sounds, cue));

        // ── The timer's words ───────────────────────────────────────────────
        foreach (var (key, what, fallback) in TimerTexts.Labels)
            entries.Add(Text("text." + key, $"The wording of {what}; \"\" for the built-in \"{fallback}\".", TimerTexts.MaxLabel,
                () => texts.Get(key), v => texts.Set(key, v)));
        entries.Add(Text("text.note",
            "A line of the user's own, shown on the timer after the status. The status line is narrow: keep it to about 18 characters ('{left} to go', 'ship it'), or it is cut off. Placeholders fill in live: "
            + string.Join(", ", TimerTexts.Placeholders.Select(p => $"{p.Token} ({p.Meaning})")) + ". \"\" for none.",
            TimerTexts.MaxNote, () => texts.Note, v => texts.Note = v));
        entries.Add(Text("text.quote", "For the Motivational theme: a quote of the user's own, shown instead of the rotating ones; \"\" for those.",
            160, () => texts.Quote, v => texts.Quote = v));

        // ── Look details ────────────────────────────────────────────────────
        entries.Add(Choice("appearance.digit_weight", "How heavy the digits are.",
            AvaloniaYapaThemeSettings.DigitWeights.Select(w => (w, w)).ToArray(), () => look.DigitWeight, v => look.DigitWeight = v));
        entries.Add(Choice("appearance.digit_font", "The digits' typeface.",
            [("inter", "Inter, the classic timer face"), ("geist", "Geist, sharper and more modern")], () => look.DigitFont, v => look.DigitFont = v));
        entries.Add(AutoColor("appearance.progress_color", "Colour of the progress line during pomodoros; \"auto\" follows the text colour.",
            () => look.ProgressColor, v => look.ProgressColor = v));
        entries.Add(Color("appearance.break_color", "Colour of the progress line during breaks.", () => look.BreakColor, v => look.BreakColor = v));
        entries.Add(Bool("appearance.backdrop", "A rounded plate behind the timer (on the Motivational theme, its card's colour).",
            () => look.Backdrop, v => look.Backdrop = v));
        entries.Add(Color("appearance.backdrop_color", "Colour of that plate.", () => look.BackdropColor, v => look.BackdropColor = v));
        entries.Add(Percent("appearance.backdrop_opacity", "How solid the plate is.", () => look.BackdropOpacity, v => look.BackdropOpacity = v));

        // ── Music and own files ─────────────────────────────────────────────
        entries.Add(Media("music.during_pomodoros", "Music that plays during pomodoros.", true, () => music.WorkSong, v => music.WorkSong = v));
        entries.Add(Bool("music.repeat_during_pomodoros", "Loop the pomodoro music.", () => music.RepeatWorkSong, v => music.RepeatWorkSong = v));
        entries.Add(Media("music.during_breaks", "Music that plays during short breaks.", true, () => music.BreakSong, v => music.BreakSong = v));
        entries.Add(Bool("music.repeat_during_breaks", "Loop the break music.", () => music.RepeatBreakSong, v => music.RepeatBreakSong = v));
        entries.Add(Media("music.during_long_break", "Music that plays during the long break.", true, () => music.SessionBreakSong, v => music.SessionBreakSong = v));
        entries.Add(Bool("music.repeat_during_long_break", "Loop the long-break music.", () => music.RepeatSessionBreakSong, v => music.RepeatSessionBreakSong = v));
        entries.Add(Media("ambience.recording_file", "The user's own recording for the \"own_recording\" background sound.", false,
            () => ambience.FilePath, v =>
            {
                ambience.FilePath = v;
                if (v == null && ambience.Sound == AmbientSound.File) ambience.Sound = AmbientSound.None;
            }));

        _entries = entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SettingEntry> Entries => _entries.Values;

    /// <summary>Every setting with its current value, for the model to read.</summary>
    public JsonArray Describe(string? prefix = null) => new(_entries.Values
        .Where(e => string.IsNullOrEmpty(prefix) || e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Select(e =>
        {
            var o = new JsonObject { ["key"] = e.Key, ["value"] = e.Get(), ["type"] = e.Type, ["description"] = e.Description };
            if (e.Constraint != null) o["allowed"] = e.Constraint;
            return (JsonNode)o;
        }).ToArray());

    public JsonNode? Get(string key) => Find(key).Get();

    // How a change reads in the panel ("Set rain volume to 50%"): plain words, not keys.
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["timer.work_minutes"] = "pomodoro length",
        ["timer.short_break_minutes"] = "short break",
        ["timer.long_break_minutes"] = "long break",
        ["timer.pomodoros_per_cycle"] = "pomodoros per cycle",
        ["timer.auto_start_breaks"] = "auto-start breaks",
        ["timer.auto_start_pomodoros"] = "auto-start pomodoros",
        ["timer.count_down"] = "count down",
        ["timer.counter"] = "counter",
        ["goal.daily_goal"] = "daily goal",
        ["goal.show_forecast"] = "goal forecast",
        ["sound.enabled"] = "sounds",
        ["sound.volume"] = "sound volume",
        ["ambience.enabled"] = "background sound",
        ["ambience.sound"] = "background sound",
        ["ambience.volume"] = "background volume",
        ["ambience.ticking"] = "ticking",
        ["ambience.tick_volume"] = "ticking volume",
        ["ambience.during_breaks"] = "sound during breaks",
        ["ambience.wind_down"] = "wind-down",
        ["appearance.theme"] = "theme",
        ["appearance.size"] = "timer size",
        ["appearance.opacity"] = "timer opacity",
        ["appearance.text_color"] = "text colour",
        ["appearance.shadow_color"] = "shadow colour",
        ["appearance.shadow_opacity"] = "shadow",
        ["appearance.show_seconds"] = "seconds",
        ["appearance.show_status"] = "status line",
        ["appearance.show_progress"] = "progress line",
        ["appearance.controls_on_hover"] = "buttons only on hover",
        ["appearance.flash_when_done"] = "flash when done",
        ["appearance.digit_weight"] = "digit weight",
        ["appearance.digit_font"] = "digit font",
        ["appearance.progress_color"] = "progress colour",
        ["appearance.break_color"] = "break colour",
        ["appearance.backdrop"] = "backdrop",
        ["appearance.backdrop_color"] = "backdrop colour",
        ["appearance.backdrop_opacity"] = "backdrop strength",
        ["text.quote"] = "your quote",
        ["window.always_on_top"] = "always on top",
        ["window.all_workspaces"] = "all workspaces",
        ["window.stay_on_show_desktop"] = "stay on Show Desktop",
        ["window.show_in_taskbar"] = "show in the dock",
        ["window.avoid_shell_panels"] = "keep clear of panels",
        ["window.minimize_to_tray"] = "hide to tray",
        ["general.pause_when_locked"] = "pause when locked",
        ["general.desktop_notifications"] = "notifications",
        ["general.launch_at_login"] = "launch at login",
        ["general.start_hidden"] = "start hidden",
        ["assistant.model"] = "assistant model",
        ["music.during_pomodoros"] = "pomodoro music",
        ["music.repeat_during_pomodoros"] = "loop pomodoro music",
        ["music.during_breaks"] = "break music",
        ["music.repeat_during_breaks"] = "loop break music",
        ["music.during_long_break"] = "long-break music",
        ["music.repeat_during_long_break"] = "loop long-break music",
        ["ambience.recording_file"] = "recording",
        ["text.note"] = "the timer's note",
    };

    /// <summary>"rain volume to 50%", "auto-start breaks on".</summary>
    public string Describe(string key, JsonNode? value)
    {
        var entry = Find(key);
        var label = Labels.TryGetValue(entry.Key, out var l) ? l
            : entry.Key.StartsWith("sound.cue.", StringComparison.Ordinal) ? $"{entry.Key[10..].Replace('_', ' ')} sound"
            : entry.Key.StartsWith("text.", StringComparison.Ordinal)
                ? TimerTexts.Labels.FirstOrDefault(t => "text." + t.Key == entry.Key).What?.Replace("the ", "") ?? entry.Key
            : entry.Key[(entry.Key.IndexOf('.') + 1)..].Replace('_', ' ');
        var text = value is JsonValue v && v.TryGetValue<string>(out var str) ? str : value?.ToJsonString() ?? "";
        if (entry.Type == "text") return text.Length == 0 ? $"{label} back to the default" : $"{label} to \"{text}\"";
        if (entry.Type is "media" or "file" or "cue" && text != "none")
        {
            text = YAPA.Avalonia.Audio.Library.MediaLibrary.Radio.FirstOrDefault(r => r.Reference == text)?.Name
                ?? (text.StartsWith('/') ? YAPA.Avalonia.Audio.Library.MediaLibrary.NameFor(text) : text);
        }
        return entry.Type switch
        {
            "bool" => $"{label} {(text == "true" ? "on" : "off")}",
            "percent" => $"{label} to {text}%",
            "integer" when entry.Key.EndsWith("_minutes", StringComparison.Ordinal) => $"{label} to {text} min",
            "color" or "color_auto" => $"{label} to {(text == "auto" ? "follow the text" : ColorName(text))}",
            _ => $"{label} to {text.Replace('_', ' ').Replace('-', ' ')}",
        };
    }

    private static string ColorName(string hex)
    {
        if (!global::Avalonia.Media.Color.TryParse(hex, out var c)) return hex;
        foreach (var p in typeof(Colors).GetProperties())
            if (p.GetValue(null) is Color named && named == c)
                return System.Text.RegularExpressions.Regex.Replace(p.Name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>
    /// Applies all the changes or none: every value is checked first, against a scratch run of
    /// the setters' own parsing, so one bad value doesn't leave half a request applied.
    /// </summary>
    /// <returns>Each change as {key, from, to}.</returns>
    public JsonArray Apply(JsonObject changes, ChangeJournal? journal)
    {
        if (changes.Count == 0) throw new ToolException("No settings were given.");

        var plan = changes.Select(c => (Entry: Find(c.Key), Value: c.Value?.DeepClone())).ToList();
        foreach (var (entry, value) in plan) Validate(entry, value);

        var applied = new JsonArray();
        foreach (var (entry, value) in plan)
        {
            var before = entry.Get();
            if (JsonNode.DeepEquals(before, Normalize(entry, value))) continue;
            entry.Set(value);
            var after = entry.Get();
            journal?.Record(() => entry.Set(before));
            applied.Add(new JsonObject { ["key"] = entry.Key, ["from"] = before, ["to"] = after });
        }
        return applied;
    }

    /// <summary>
    /// Sets each value back, skipping any that no longer applies (a sound file since deleted):
    /// one stale value mustn't stop the rest of an undo. Twice, because some values depend on
    /// others (the own-recording sound needs its file first).
    /// </summary>
    public void Restore(JsonObject values)
    {
        var pending = values.ToList();
        for (var pass = 0; pass < 2 && pending.Count > 0; pass++)
        {
            var failed = new List<KeyValuePair<string, JsonNode?>>();
            foreach (var (key, value) in pending)
            {
                try
                {
                    var entry = Find(key);
                    if (!JsonNode.DeepEquals(entry.Get(), value)) entry.Set(value?.DeepClone());
                }
                catch (ToolException)
                {
                    failed.Add(new(key, value));
                }
            }
            pending = failed;
        }
    }

    /// <summary>The value as the setting itself would report it once set ("#bfe9ff" → "#ffbfe9ff"); throws if invalid.</summary>
    public JsonNode? Normalized(string key, JsonNode? value) => Normalize(Find(key), value);

    /// <summary>Throws, as <see cref="Apply"/> would, if any value isn't valid; changes nothing.</summary>
    public void Validate(JsonObject changes)
    {
        foreach (var (key, value) in changes) Validate(Find(key), value);
    }

    private SettingEntry Find(string key)
        => _entries.TryGetValue(key.Trim(), out var entry)
            ? entry
            : throw new ToolException($"There is no setting called \"{key}\". Call get_settings for the list.");

    private static void Validate(SettingEntry entry, JsonNode? value) => _ = Normalize(entry, value);

    /// <summary>The value in the form <see cref="SettingEntry.Get"/> returns it; throws when it isn't valid.</summary>
    private static JsonNode? Normalize(SettingEntry entry, JsonNode? value) => entry.Type switch
    {
        "bool" => ParseBool(entry, value),
        "integer" => ParseInt(entry, value, int.MinValue, int.MaxValue),
        "percent" => ParsePercent(entry, value),
        "color" => Hex(ParseColor(entry, value)),
        "cue" => ParseCue(entry, value),
        "text" => ParseText(entry, value),
        "color_auto" => ParseAutoColor(entry, value) is { } c ? Hex(c) : "auto",
        "media" or "file" => ParseMedia(entry, value) ?? "none",
        _ => ParseChoice(entry, value),
    };

    // ── Entry builders ──────────────────────────────────────────────────────

    private static SettingEntry Bool(string key, string description, Func<bool> get, Action<bool> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "bool", null, () => get(), v => set(ParseBool(self!, v)));
        return self;
    }

    private static SettingEntry Int(string key, string description, int min, int max, Func<int> get, Action<int> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "integer", $"{min} to {max}", () => get(), v => set(ParseInt(self!, v, min, max)));
        return self;
    }

    /// <summary>Stored as 0..1, shown and set as a percentage, as the settings window shows it.</summary>
    private static SettingEntry Percent(string key, string description, Func<double> get, Action<double> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "percent", "0 to 100",
            () => (int)Math.Round(get() * 100), v => set(Math.Round(ParsePercent(self!, v) / 100.0, 2)));
        return self;
    }

    private static SettingEntry Choice(string key, string description, (string Value, string Meaning)[] choices, Func<string> get, Action<string> set)
    {
        SettingEntry? self = null;
        var constraint = string.Join("; ", choices.Select(c => $"{c.Value} ({c.Meaning})"));
        self = new SettingEntry(key, description, "choice", constraint, () => get(), v => set(ParseChoice(self!, v)));
        return self;
    }

    private static SettingEntry Color(string key, string description, Func<Color> get, Action<Color> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "color", "#RRGGBB, or a colour name such as white, tomato, gold",
            () => Hex(get()), v => set(ParseColor(self!, v)));
        return self;
    }

    private static SettingEntry Cue(SoundSettings sounds, SoundCue cue)
    {
        var names = SoundRef.BuiltIns.Select(s => s.Serialize().Replace("builtin:", "")).ToArray();
        SettingEntry? self = null;
        self = new SettingEntry(
            "sound.cue." + cue switch
            {
                SoundCue.Start => "start",
                SoundCue.Pause => "pause",
                SoundCue.Resume => "resume",
                SoundCue.Stop => "stop",
                SoundCue.WorkCompleted => "pomodoro_done",
                _ => "break_over",
            },
            $"Sound for \"{SoundSettings.Label(cue)}\".",
            "cue",
            "none; " + string.Join("; ", names) + "; or an audio file: one of the user's Sounds (list_sounds) or an absolute path",
            () => sounds.Get(cue) switch
            {
                { IsNone: true } => "none",
                { FilePath: { } path } => path,
                var s => s.Serialize().Replace("builtin:", ""),
            },
            v =>
            {
                var value = ParseCue(self!, v);
                sounds.Set(cue, value == "none" ? SoundRef.None
                    : value.StartsWith('/') ? SoundRef.Parse("file:" + value)
                    : SoundRef.Parse("builtin:" + value));
            });
        return self;
    }

    private static SettingEntry AutoColor(string key, string description, Func<Color?> get, Action<Color?> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "color_auto", "auto, #RRGGBB, or a colour name",
            () => get() is { } c ? Hex(c) : "auto", v => set(ParseAutoColor(self!, v)));
        return self;
    }

    /// <summary>
    /// Always "#aarrggbb": Avalonia's Color.ToString gives a name when one matches ("White"),
    /// which would make the same colour read two ways.
    /// </summary>
    private static string Hex(Color c) => $"#{c.A:x2}{c.R:x2}{c.G:x2}{c.B:x2}";

    private static Color? ParseAutoColor(SettingEntry entry, JsonNode? value)
        => value is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is "auto" or "" or "text" or "default"
            ? null
            : ParseColor(entry, value);

    private static SettingEntry Text(string key, string description, int max, Func<string> get, Action<string> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, "text", $"up to {max} characters", () => get(), v => set(ParseText(self!, v)));
        return self;
    }

    private static string ParseText(SettingEntry entry, JsonNode? value)
    {
        var text = value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
            var other => other.ToString().Trim(),
        };
        var max = int.Parse(entry.Constraint!.Split(' ')[2], CultureInfo.InvariantCulture);
        if (text.Length > max) throw new ToolException($"{entry.Key} can be at most {max} characters.");
        return text;
    }

    /// <param name="radio">Radio stations allowed too (music), not only files.</param>
    private static SettingEntry Media(string key, string description, bool radio, Func<string?> get, Action<string?> set)
    {
        SettingEntry? self = null;
        self = new SettingEntry(key, description, radio ? "media" : "file",
            radio ? "none; a radio station (list_sounds); one of the user's Sounds; or an absolute path to an audio file"
                  : "none; one of the user's Sounds (list_sounds); or an absolute path to an audio file",
            () => get() is { Length: > 0 } stored ? stored : "none",
            v => set(ParseMedia(self!, v)));
        return self;
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    /// <summary>A built-in cue name, "none", or the absolute path of an audio file.</summary>
    private static string ParseCue(SettingEntry entry, JsonNode? value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(text)) throw Invalid(entry, value);
        var builtIn = SoundRef.BuiltIns.Select(b => b.Serialize().Replace("builtin:", ""))
            .FirstOrDefault(n => n.Replace('-', '_') == text.ToLowerInvariant().Replace(' ', '_').Replace('-', '_'));
        if (builtIn != null) return builtIn;
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        return ResolveFile(text) ?? throw Invalid(entry, value);
    }

    /// <returns>The stored reference ("radio:…" or a path), or null for none.</returns>
    private static string? ParseMedia(SettingEntry entry, JsonNode? value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.Type == "media")
        {
            var key = text.ToLowerInvariant().Replace("radio:", "").Replace(' ', '-').Replace('_', '-');
            var station = YAPA.Avalonia.Audio.Library.MediaLibrary.Radio.FirstOrDefault(r =>
                r.Reference == "radio:" + key || r.Name.ToLowerInvariant().Replace(' ', '-') == key);
            if (station != null) return station.Reference;
        }
        return ResolveFile(text) ?? throw Invalid(entry, value);
    }

    /// <summary>One of the user's Sounds by name or file name, or an existing absolute (or ~) path.</summary>
    private static string? ResolveFile(string text)
    {
        var library = YAPA.Avalonia.Audio.Library.MediaLibrary.LibraryFiles();
        var named = library.FirstOrDefault(f => string.Equals(f.Name, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(f.Location), text, StringComparison.OrdinalIgnoreCase));
        if (named != null) return named.Location;

        var path = text.StartsWith("file:", StringComparison.Ordinal) ? text[5..] : text;
        if (path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Path.IsPathRooted(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static ToolException Invalid(SettingEntry entry, JsonNode? value)
        => new($"{entry.Key} can't be {value?.ToJsonString() ?? "empty"}: it takes {entry.Constraint ?? entry.Type}.");

    private static bool ParseBool(SettingEntry entry, JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is var t
            && (t is "true" or "on" or "yes" or "false" or "off" or "no") => t is "true" or "on" or "yes",
        _ => throw Invalid(entry, value),
    };

    private static int ParseInt(SettingEntry entry, JsonNode? value, int min, int max)
    {
        var number = ToolArgs.AsNumber(value);
        if (number is not { } n || double.IsNaN(n)) throw Invalid(entry, value);
        var rounded = (int)Math.Round(n);
        // The entry's own range when it has one (the builders pass it); Normalize passes none.
        if (entry.Constraint is { } c && c.Split(" to ") is [var lo, var hi]
            && int.TryParse(lo, out var low) && int.TryParse(hi, out var high))
        {
            min = Math.Max(min, low);
            max = Math.Min(max, high);
        }
        if (rounded < min || rounded > max) throw Invalid(entry, value);
        return rounded;
    }

    private static int ParsePercent(SettingEntry entry, JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) value = JsonValue.Create(s.Trim().TrimEnd('%'));
        return ParseInt(entry, value, 0, 100);
    }

    private static string ParseChoice(SettingEntry entry, JsonNode? value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') : null;
        if (text == null) throw Invalid(entry, value);
        // Choices are listed as "value (meaning)"; cue names are kebab-case, hence the second form.
        var choices = (entry.Constraint ?? "").Split("; ").Select(c => c.Split(' ')[0].TrimEnd('.')).ToList();
        var match = choices.FirstOrDefault(c => c.Replace('-', '_') == text);
        return match ?? throw Invalid(entry, value);
    }

    private static Color ParseColor(SettingEntry entry, JsonNode? value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (text != null && !text.StartsWith('#') && text.Length is 6 or 8 && text.All(Uri.IsHexDigit)) text = "#" + text;
        if (text != null && global::Avalonia.Media.Color.TryParse(text, out var color))
            return text.StartsWith('#') && text.Length == 7 ? new Color(255, color.R, color.G, color.B) : color;
        throw Invalid(entry, value);
    }
}
