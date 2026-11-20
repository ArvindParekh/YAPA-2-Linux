using System;
using System.Collections.Generic;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// What the command bar offers, built fresh each time it's needed: the timer's actions as they
/// stand (Resume only while paused, Skip only where there's something to skip), then places to go.
/// The order is the order shown before anything is typed, so the likeliest come first.
/// </summary>
public static class PaletteActions
{
    public const string AskGroup = "Ask YAPA";
    public const string TimerGroup = "Timer";
    public const string GoGroup = "Go to";
    public const string SettingsGroup = "Settings";

    /// <summary>The icons, on a 24-unit grid, drawn as strokes.</summary>
    public static class Icons
    {
        public const string Play = "M 8 5.5 L 18.5 12 L 8 18.5 Z";
        public const string Pause = "M 9 6 V 18 M 15 6 V 18";
        public const string Skip = "M 6 6 L 14 12 L 6 18 Z M 18 6 V 18";
        public const string Stop = "M 7.5 6 H 16.5 A 1.5 1.5 0 0 1 18 7.5 V 16.5 A 1.5 1.5 0 0 1 16.5 18 H 7.5 A 1.5 1.5 0 0 1 6 16.5 V 7.5 A 1.5 1.5 0 0 1 7.5 6 Z";
        public const string Check = "M 4.5 12.5 L 9.5 17.5 L 19.5 6.5";
        public const string Cup = "M 5 9 H 16 V 14 A 5 5 0 0 1 11 19 H 10 A 5 5 0 0 1 5 14 Z M 16 10.5 H 17.5 A 2.25 2.25 0 0 1 17.5 15 H 16 M 8.5 3.5 V 6 M 12 3.5 V 6";
        public const string Wave = "M 3 12 H 5 M 7.5 9 V 15 M 11 5 V 19 M 14.5 8 V 16 M 18 10.5 V 13.5 M 21 12 H 21";
        public const string EyeOff = "M 4 4 L 20 20 M 10 6.2 A 9 9 0 0 1 12 6 C 17 6 21 12 21 12 C 20.4 13 19.6 14 18.6 15 M 15.5 17.3 A 8 8 0 0 1 12 18 C 7 18 3 12 3 12 C 3.9 10.6 5 9.3 6.3 8.3 M 9.9 9.9 A 3 3 0 0 0 14.1 14.1";
        public const string Eye = "M 3 12 C 3 12 7 6 12 6 C 17 6 21 12 21 12 C 21 12 17 18 12 18 C 7 18 3 12 3 12 Z M 15 12 A 3 3 0 1 1 9 12 A 3 3 0 1 1 15 12 Z";
        public const string Sun = "M 12 3.5 V 5.5 M 12 18.5 V 20.5 M 3.5 12 H 5.5 M 18.5 12 H 20.5 M 6 6 L 7.4 7.4 M 16.6 16.6 L 18 18 M 6 18 L 7.4 16.6 M 16.6 7.4 L 18 6 M 16 12 A 4 4 0 1 1 8 12 A 4 4 0 1 1 16 12 Z";
        public const string Calendar = "M 5 6 H 19 A 1.5 1.5 0 0 1 20.5 7.5 V 19 A 1.5 1.5 0 0 1 19 20.5 H 5 A 1.5 1.5 0 0 1 3.5 19 V 7.5 A 1.5 1.5 0 0 1 5 6 Z M 3.5 10.5 H 20.5 M 8 3.5 V 7.5 M 16 3.5 V 7.5";
        public const string Bars = "M 4 20 H 20 M 7 16 V 11 M 12 16 V 6 M 17 16 V 9";
        public const string Clock = "M 20 12 A 8 8 0 1 1 4 12 A 8 8 0 1 1 20 12 Z M 12 7.5 V 12 L 15 14";
        public const string Brush = "M 19.5 4.5 L 11 13 M 11 13 L 9 11 L 17.5 2.5 M 9 13.5 C 6 13.5 5 15.5 5 17.5 C 5 18.5 4 19.5 3.5 19.5 C 5 21 9.5 21 10.5 18 C 11 16.5 10.5 15 9 13.5 Z";
        public const string Speaker = "M 4 9.5 H 7.5 L 12 5.5 V 18.5 L 7.5 14.5 H 4 Z M 15.5 9 A 4 4 0 0 1 15.5 15 M 18 6.5 A 7.5 7.5 0 0 1 18 17.5";
        public const string Spark = "M 12 3.5 L 13.8 10.2 L 20.5 12 L 13.8 13.8 L 12 20.5 L 10.2 13.8 L 3.5 12 L 10.2 10.2 Z";
        public const string Book = "M 4 5.5 C 6.5 4.5 9.5 4.5 12 6 C 14.5 4.5 17.5 4.5 20 5.5 V 18.5 C 17.5 17.5 14.5 17.5 12 19 C 9.5 17.5 6.5 17.5 4 18.5 Z M 12 6 V 19";
        public const string Bell = "M 6 16.5 V 11 A 6 6 0 0 1 18 11 V 16.5 L 19.5 18 H 4.5 Z M 10 20.5 H 14";
        public const string Gear = "M 15 12 A 3 3 0 1 1 9 12 A 3 3 0 1 1 15 12 Z M 12 3 V 5.5 M 12 18.5 V 21 M 3 12 H 5.5 M 18.5 12 H 21 M 5.6 5.6 L 7.4 7.4 M 16.6 16.6 L 18.4 18.4 M 5.6 18.4 L 7.4 16.6 M 16.6 7.4 L 18.4 5.6";
        public const string Info = "M 20 12 A 8 8 0 1 1 4 12 A 8 8 0 1 1 20 12 Z M 12 11 V 16 M 12 8 V 8.2";
        public const string Window = "M 5 5 H 19 A 2 2 0 0 1 21 7 V 17 A 2 2 0 0 1 19 19 H 5 A 2 2 0 0 1 3 17 V 7 A 2 2 0 0 1 5 5 Z M 3 9 H 21";
        public const string Plus = "M 12 5 V 19 M 5 12 H 19";
        public const string Power = "M 12 3.5 V 11 M 7 6.5 A 7.5 7.5 0 1 0 17 6.5";
    }

    public static List<PaletteCommand> Build(TimerViewModel timer, AppNavigator navigator, bool timerShowing)
    {
        var list = new List<PaletteCommand>();
        var phase = timer.Phase;

        // The timer, as it stands.
        if (timer.CanStart)
            list.Add(new(timer.StartMenuText, "start begin go play resume focus pomodoro work", Icons.Play,
                () => timer.StartCommand.Execute(null), TimerGroup, "Space"));
        if (timer.CanPause)
            list.Add(new("Pause", "pause hold wait", Icons.Pause, () => timer.PauseCommand.Execute(null), TimerGroup, "Space"));
        if (timer.CanSkip)
            list.Add(new(phase is PomodoroPhase.WorkEnded ? "Skip the break" : "Skip to the next pomodoro", "skip next break", Icons.Skip,
                () => timer.SkipCommand.Execute(null), TimerGroup, "S"));
        if (timer.CanStop)
            list.Add(new(timer.StopMenuText, "stop end abandon cancel quit break", Icons.Stop, () => timer.StopCommand.Execute(null), TimerGroup, "Esc"));
        if (timer.CanLongBreak)
            list.Add(new(timer.LongBreakMenuText, "long break rest coffee", Icons.Cup, () => timer.LongBreakCommand.Execute(null), TimerGroup, "L"));
        if (timer.CanMarkDone)
            list.Add(new("Mark a pomodoro done", "mark done finished log credit complete", Icons.Check, () => timer.MarkDoneCommand.Execute(null), TimerGroup));

        // Places.
        list.Add(new("Today", "day journal today what did i do your day", Icons.Sun, () => navigator.ShowDay(), GoGroup, "Ctrl D"));
        list.Add(new("This week", "week journal review summary", Icons.Calendar, navigator.ShowWeek, GoGroup));
        list.Add(new("Insights", "insights statistics stats history heatmap streak chart", Icons.Bars, navigator.ShowStatistics, GoGroup, "Ctrl I"));

        // Switches.
        if (timer.AmbienceConfigured)
            list.Add(new(timer.AmbienceOn ? "Turn ambience off" : "Turn ambience on", "ambience sound music noise rain mute " + timer.AmbienceMenuText,
                Icons.Wave, () => timer.ToggleAmbienceCommand.Execute(null), TimerGroup));
        if (timer.CanStartOffRecord)
            list.Add(new("Go off the record", "off record private pause journal incognito hide", Icons.EyeOff,
                () => timer.GoOffRecordCommand.Execute(null), TimerGroup));
        else if (timer.IsOffRecord)
            list.Add(new(timer.BackOnRecordText, "back on record journal resume", Icons.Eye, () => timer.BackOnRecordCommand.Execute(null), TimerGroup));
        list.Add(new(timerShowing ? "Hide the timer" : "Show the timer", "timer window hide show tray minimise", Icons.Window,
            timerShowing ? navigator.HideTimer : navigator.ShowTimer, TimerGroup, timerShowing ? "Ctrl H" : ""));

        // Settings, by page.
        list.Add(new("Timer settings", "settings preferences durations length minutes goal cycle", Icons.Clock, () => navigator.ShowSettings(SettingsPage.Timer), SettingsGroup, "Ctrl ,"));
        list.Add(new("Appearance", "settings theme look size colour color font studio classic", Icons.Brush, () => navigator.ShowSettings(SettingsPage.Appearance), SettingsGroup));
        list.Add(new("Sounds", "settings audio volume ambience music tick alarm", Icons.Speaker, () => navigator.ShowSettings(SettingsPage.Sounds), SettingsGroup));
        list.Add(new("Ask YAPA settings", "settings assistant claude sign memory scheduled", Icons.Spark, () => navigator.ShowSettings(SettingsPage.Assistant), SettingsGroup));
        list.Add(new("Your day settings", "settings journal privacy forget browser sources", Icons.Book, () => navigator.ShowSettings(SettingsPage.Journal), SettingsGroup));
        list.Add(new("Notifications", "settings notify alerts reminders", Icons.Bell, () => navigator.ShowSettings(SettingsPage.Notifications), SettingsGroup));
        list.Add(new("General", "settings startup tray workspaces language", Icons.Gear, () => navigator.ShowSettings(SettingsPage.General), SettingsGroup));
        list.Add(new("Keyboard shortcuts", "about help keys shortcuts version", Icons.Info, () => navigator.ShowSettings(SettingsPage.About), SettingsGroup));
        list.Add(new("Quit YAPA", "quit exit close", Icons.Power, navigator.Quit, SettingsGroup, "Ctrl Q"));
        return list;
    }

    /// <summary>
    /// What shows before anything is typed: the timer's next step, then questions, then places.
    /// The timer comes first because Enter on it is cheap and undoable; Enter on a question spends a request.
    /// </summary>
    public static List<PaletteCommand> Opening(IEnumerable<PaletteCommand> questions, IReadOnlyList<PaletteCommand> actions)
    {
        var list = new List<PaletteCommand>();
        foreach (var a in actions)
            if (a.Group == TimerGroup && list.Count < 2 && a.Icon is Icons.Play or Icons.Pause or Icons.Skip) list.Add(a);
        list.AddRange(questions);
        foreach (var a in actions)
            if (a.Group == GoGroup) list.Add(a);
        return list;
    }
}
