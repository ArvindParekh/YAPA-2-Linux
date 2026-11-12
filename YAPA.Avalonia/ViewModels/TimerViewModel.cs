using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.ViewModels;

public enum TimerAlert
{
    None,
    WorkEnded,
    BreakEnded,
}

/// <summary>
/// Everything a timer window shows and every action it can take. Both themes
/// bind to the same instance, as do the tray menu and keyboard shortcuts, so
/// they can never disagree about state or about what an action means.
/// </summary>
public sealed partial class TimerViewModel : ObservableObject
{
    private readonly IPomodoroEngine _engine;
    private readonly AvaloniaYapaThemeSettings _theme;
    private readonly IAppNavigator _navigator;
    private readonly PomodoroEngineSettings _engineSettings;
    private readonly YAPA.Avalonia.Audio.Ambience.AmbienceSettings _ambience;
    private readonly GeneralSettings _general;
    private readonly IPomodoroRepository _repository;
    private readonly IClock _clock;
    private readonly ITimer _forecastTimer;

    /// <param name="forecastTimer">
    /// Moves the goal forecast on while nothing else ticks (idle, paused): the time of day
    /// it gives slides later for every minute nobody is working.
    /// </param>
    public TimerViewModel(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, AvaloniaYapaThemeSettings theme,
        IAppNavigator navigator, YAPA.Avalonia.Audio.Ambience.AmbienceSettings ambience, GeneralSettings general,
        IPomodoroRepository repository, IClock clock, ITimer forecastTimer)
    {
        _engine = engine;
        _engineSettings = engineSettings;
        _theme = theme;
        _navigator = navigator;
        _ambience = ambience;
        _general = general;
        _repository = repository;
        _clock = clock;
        _forecastTimer = forecastTimer;
        _forecastTimer.Tick += () =>
        {
            RefreshGoal(requery: false);
            _forecastTimer.ScheduleNext(ForecastRefresh);
        };
        _general.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GeneralSettings.DailyGoal) or nameof(GeneralSettings.ShowGoalForecast)) Refresh();
        };
        _ambience.PropertyChanged += (_, _) => RefreshAmbience();
        RefreshAmbience();

        _engine.PropertyChanged += OnEngineChanged;
        _theme.PropertyChanged += OnThemeChanged;
        // Today's count can change with no transition at all (the assistant edits history).
        _repository.Changed += _ => Refresh();
        Refresh();
    }

    public IPomodoroEngine Engine => _engine;

    // ── Display ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _minutes = "00";
    [ObservableProperty] private string _seconds = "00";
    [ObservableProperty] private string _timeText = "00:00";
    [ObservableProperty] private bool _showSeconds = true;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _counterText = "1";
    [ObservableProperty] private string _counterTooltip = "";

    /// <summary>" · Cycle 2" after the status, when the counter shows the cycle; else empty.</summary>
    [ObservableProperty] private string _cycleSuffix = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private PomodoroPhase _phase;
    [ObservableProperty] private bool _isBreakInterval;
    [ObservableProperty] private TimerAlert _alert;
    [ObservableProperty] private bool _isWorkEndedAlert;
    [ObservableProperty] private bool _isBreakEndedAlert;
    [ObservableProperty] private bool _isAlerting;

    /// <summary>The ended interval has been waiting at least a second; the time shows the overrun.</summary>
    [ObservableProperty] private bool _isOverrun;

    // ── Daily goal ───────────────────────────────────────────────────────────

    private static readonly TimeSpan ForecastRefresh = TimeSpan.FromSeconds(15);
    private int _doneToday;
    private DateTime _doneDate;

    [ObservableProperty] private GoalOutlook _goal = GoalOutlook.None;

    /// <summary>"12 by 18:40" on the timer; empty when hidden or there's nothing to forecast.</summary>
    [ObservableProperty] private string _goalForecastText = "";
    [ObservableProperty] private bool _hasGoalForecast;
    [ObservableProperty] private bool _hasGoal;
    [ObservableProperty] private string _goalSummary = "";
    [ObservableProperty] private string _goalWithoutLongBreaks = "";
    [ObservableProperty] private bool _hasGoalWithoutLongBreaks;

    // ── Controls ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private string _startLabel = "START";
    [ObservableProperty] private string _startTooltip = "Start a pomodoro";
    [ObservableProperty] private string _startMenuText = "Start";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _stopTooltip = "Stop";
    [ObservableProperty] private string _stopMenuText = "Stop";
    [ObservableProperty] private bool _canSkip;
    [ObservableProperty] private bool _canReset;
    [ObservableProperty] private bool _canMarkDone;
    [ObservableProperty] private bool _canLongBreak;
    [ObservableProperty] private string _longBreakMenuText = "Take long break now";

    /// <summary>One-line summary for the tray tooltip and accessibility.</summary>
    [ObservableProperty] private string _summary = "YAPA 2";

    [RelayCommand]
    private void Start() => _engine.Start();

    [RelayCommand]
    private void Pause() => _engine.Pause();

    /// <summary>The "do the obvious thing" action: Space, tray click, <c>--toggle</c>.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (_engine.IsRunning) _engine.Pause();
        else _engine.Start();
    }

    [RelayCommand]
    private void Stop() => _engine.Stop();

    [RelayCommand]
    private void Skip() => _engine.Skip();

    [RelayCommand]
    private void Reset() => _engine.Reset();

    /// <summary>For a pomodoro done without the timer running: credits it and its break.</summary>
    [RelayCommand]
    private void MarkDone() => _engine.MarkDone();

    /// <summary>A click on the timer after an interval ends: "seen it", so stop counting and pulsing.</summary>
    [RelayCommand]
    private void StopOverrun() => _engine.StopOverrun();

    [RelayCommand]
    private void LongBreak() => _engine.StartLongBreak();

    // ── Ambience quick switch ────────────────────────────────────────────────

    /// <summary>Whether there's any ambience configured to switch on or off.</summary>
    [ObservableProperty] private bool _ambienceConfigured;
    [ObservableProperty] private bool _ambienceOn;
    [ObservableProperty] private string _ambienceMenuText = "Ambience";

    [RelayCommand]
    private void ToggleAmbience()
    {
        if (!_ambience.HasAnything)
        {
            // Nothing chosen yet: switching it "on" means picking a sound first.
            _navigator.ShowSettings(SettingsPage.Sounds);
            return;
        }
        _ambience.Enabled = !_ambience.Enabled;
    }

    private void RefreshAmbience()
    {
        AmbienceConfigured = _ambience.HasAnything;
        AmbienceOn = _ambience.Enabled && _ambience.HasAnything;
        var what = _ambience.Sound switch
        {
            YAPA.Avalonia.Audio.Ambience.AmbientSound.None => _ambience.Ticking ? "Ticking clock" : "Ambience",
            var s => YAPA.Avalonia.Audio.Ambience.AmbienceGenerators.DisplayName(s) + (_ambience.Ticking ? " + ticking" : ""),
        };
        AmbienceMenuText = AmbienceConfigured ? what : "Ambience (choose a sound…)";
    }

    [RelayCommand]
    private void OpenSettings() => _navigator.ShowSettings();

    [RelayCommand]
    private void OpenStatistics() => _navigator.ShowStatistics();

    [RelayCommand]
    private void OpenAssistant() => _navigator.ShowAssistant();

    [RelayCommand]
    private void HideToTray() => _navigator.HideTimer();

    [RelayCommand]
    private void Quit() => _navigator.Quit();

    // ── Refresh ──────────────────────────────────────────────────────────────

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Time properties change every second; the counter can hit the database,
        // so only the time-dependent parts are recomputed on ticks.
        if (e.PropertyName is nameof(IPomodoroEngine.DisplayValue) or nameof(IPomodoroEngine.Overrun))
        {
            Progress = _engine.Progress;
            UpdateTime(_engine.DisplayValue);
            UpdateSummary();
            // While running the forecast stands still; while waiting it moves on each second.
            if (e.PropertyName is nameof(IPomodoroEngine.Overrun)) RefreshGoal(requery: false);
        }
        else if (e.PropertyName is not (nameof(IPomodoroEngine.Elapsed) or nameof(IPomodoroEngine.Remaining) or nameof(IPomodoroEngine.Progress)))
        {
            // A transition raises a burst of these; only Counter means today's count may have moved.
            Refresh(requeryGoal: e.PropertyName == nameof(IPomodoroEngine.Counter));
        }
    }

    private void UpdateSummary()
        => Summary = Phase is PomodoroPhase.NotStarted ? "YAPA 2 · Ready" : $"YAPA 2 · {StatusText} · {TimeText}";

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AvaloniaYapaThemeSettings.HideSeconds)
            or nameof(AvaloniaYapaThemeSettings.DisableFlashingAnimation))
        {
            Refresh();
        }
    }

    /// <summary>
    /// Each count gets the denominator that gives it meaning: the cycle length for the
    /// position in the cycle ("2/4"), the daily goal for today's count ("5/12"). Mixing
    /// them ("3/12" beside the position in the cycle) would read as progress it isn't.
    /// </summary>
    private void RefreshCounter(PomodoroPhase phase)
    {
        var counter = _engine.Counter;
        switch (_engineSettings.Counter)
        {
            case CounterEnum.CompletedToday:
                var goal = _general.DailyGoal;
                CounterText = goal > 0 ? $"{counter}/{goal}" : counter.ToString();
                CounterTooltip = goal > 0 ? $"{counter} of {goal} pomodoros done today" : "Pomodoros completed today";
                CycleSuffix = "";
                break;
            case CounterEnum.CompletedThisSession:
                CounterText = counter.ToString();
                CounterTooltip = "Pomodoros completed since YAPA started";
                CycleSuffix = "";
                break;
            default:
                var length = Math.Max(1, _engineSettings.PomodorosBeforeLongBreak);
                var cycle = _engine.Cycle;
                CounterText = $"{_engine.Index}/{length}";
                CounterTooltip = $"Pomodoro {_engine.Index} of {length}, cycle {cycle}";
                // A fresh start has no cycle worth naming yet.
                var fresh = phase == PomodoroPhase.NotStarted && _engine.Index == 1 && cycle == 1;
                CycleSuffix = fresh ? "" : $" · Cycle {cycle}";
                break;
        }
    }

    /// <summary>Recomputes everything, today's count included.</summary>
    public void Refresh() => Refresh(requeryGoal: true);

    private void Refresh(bool requeryGoal)
    {
        var phase = _engine.Phase;
        var paused = phase == PomodoroPhase.Pause;
        var pausedBreak = paused && _engine.PausedPhase == PomodoroPhase.Break;
        var inBreak = phase is PomodoroPhase.Break or PomodoroPhase.BreakEnded || pausedBreak;

        Phase = phase;
        IsBreakInterval = inBreak;
        Progress = _engine.Progress;
        RefreshCounter(phase);

        UpdateTime(_engine.DisplayValue);

        StatusText = phase switch
        {
            PomodoroPhase.NotStarted => "Ready",
            PomodoroPhase.Work => "Work",
            PomodoroPhase.WorkEnded => "Work done",
            PomodoroPhase.Break => _engine.IsLongBreak ? "Long break" : "Break",
            PomodoroPhase.BreakEnded => "Break over",
            PomodoroPhase.Pause => pausedBreak ? "Break paused" : "Paused",
            _ => string.Empty,
        };

        CanStart = !_engine.IsRunning;
        (StartLabel, StartMenuText, StartTooltip) = phase switch
        {
            PomodoroPhase.Pause => ("RESUME", "Resume", pausedBreak ? "Resume the break (Space)" : "Resume the pomodoro (Space)"),
            PomodoroPhase.WorkEnded => ("BREAK", "Start break", _engine.IsLongBreak ? "Start the long break (Space)" : "Start the break (Space)"),
            _ => ("START", "Start pomodoro", "Start a pomodoro (Space)"),
        };
        CanPause = _engine.IsRunning;
        CanStop = phase != PomodoroPhase.NotStarted;
        var stoppingBreak = inBreak || phase == PomodoroPhase.WorkEnded;
        StopTooltip = stoppingBreak ? "End the break (Esc)" : "Abandon this pomodoro (Esc)";
        StopMenuText = stoppingBreak ? "End break" : "Abandon pomodoro";
        CanSkip = phase is PomodoroPhase.WorkEnded or PomodoroPhase.Break or PomodoroPhase.BreakEnded || pausedBreak;
        CanReset = !(phase == PomodoroPhase.NotStarted && _engine.Index == 1);
        CanMarkDone = _engine.CanMarkDone;
        CanLongBreak = _engine.CanStartLongBreak;
        LongBreakMenuText = phase is PomodoroPhase.Break || pausedBreak ? "Make this a long break" : "Take long break now";

        var flashing = !_theme.DisableFlashingAnimation;
        Alert = phase switch
        {
            PomodoroPhase.WorkEnded => TimerAlert.WorkEnded,
            PomodoroPhase.BreakEnded => TimerAlert.BreakEnded,
            _ => TimerAlert.None,
        };
        // The pulse is a call for attention; once the overrun is stopped it has been answered.
        var calling = flashing && !_engine.OverrunStopped;
        IsWorkEndedAlert = calling && Alert == TimerAlert.WorkEnded;
        IsBreakEndedAlert = calling && Alert == TimerAlert.BreakEnded;
        IsAlerting = IsWorkEndedAlert || IsBreakEndedAlert;

        UpdateSummary();
        RefreshGoal(requeryGoal);

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        LongBreakCommand.NotifyCanExecuteChanged();
    }

    /// <param name="requery">Read today's count again (it can hit the database); else reuse it, unless the day changed.</param>
    public void RefreshGoal(bool requery = true)
    {
        var goal = _general.DailyGoal;
        var now = _clock.UtcNow;
        if (goal > 0 && (requery || now.ToLocalTime().Date != _doneDate))
        {
            _doneToday = _repository.CompletedToday();
            _doneDate = now.ToLocalTime().Date;
        }

        Goal = GoalOutlook.Compute(_engine, _engineSettings, goal, _doneToday, now);
        HasGoal = Goal.HasGoal;
        GoalSummary = Goal.Summary;
        GoalWithoutLongBreaks = Goal.WithoutLongBreaks;
        HasGoalWithoutLongBreaks = GoalWithoutLongBreaks.Length > 0;
        GoalForecastText = _general.ShowGoalForecast ? Goal.Short : "";
        HasGoalForecast = GoalForecastText.Length > 0;

        // Running intervals and ended ones tick the engine already; only idle and paused time needs
        // this. It keeps going once the goal is reached, so a new day still brings a fresh count.
        if (Goal.HasGoal && _engine.Phase is PomodoroPhase.NotStarted or PomodoroPhase.Pause)
        {
            _forecastTimer.Start();
            _forecastTimer.ScheduleNext(ForecastRefresh);
        }
        else
        {
            _forecastTimer.Stop();
        }
    }

    private void UpdateTime(int totalSeconds)
    {
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        ShowSeconds = !_theme.HideSeconds;

        Minutes = minutes.ToString("00");
        Seconds = seconds.ToString("00");

        // Past the end of an interval nobody has moved on from, the clock counts the
        // overrun instead of sitting at 00:00: that's the time the day is losing.
        IsOverrun = _engine.Overrun is { } over && (ShowSeconds ? over >= 1 : over >= 60);
        if (IsOverrun)
        {
            TimeText = FormatOverrun(_engine.Overrun!.Value, ShowSeconds);
            return;
        }

        if (ShowSeconds)
        {
            TimeText = $"{Minutes}:{Seconds}";
        }
        else
        {
            // Without seconds, the last minute would read "00" while time is still
            // left; show "<1" so it's clear the interval isn't over yet.
            TimeText = minutes == 0 && seconds > 0 ? "<1" : Minutes;
        }
    }

    /// <summary>"+04:12"; without seconds "+4"; past an hour just "+1h", which says enough.</summary>
    internal static string FormatOverrun(int seconds, bool showSeconds)
    {
        if (seconds >= 3600) return $"+{seconds / 3600}h";
        return showSeconds ? $"+{seconds / 60:00}:{seconds % 60:00}" : $"+{seconds / 60}";
    }
}
