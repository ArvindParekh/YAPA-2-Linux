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

    public TimerViewModel(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, AvaloniaYapaThemeSettings theme,
        IAppNavigator navigator, YAPA.Avalonia.Audio.Ambience.AmbienceSettings ambience, GeneralSettings general)
    {
        _engine = engine;
        _engineSettings = engineSettings;
        _theme = theme;
        _navigator = navigator;
        _ambience = ambience;
        _general = general;
        _general.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettings.DailyGoal)) Refresh();
        };
        _ambience.PropertyChanged += (_, _) => RefreshAmbience();
        RefreshAmbience();

        _engine.PropertyChanged += OnEngineChanged;
        _theme.PropertyChanged += OnThemeChanged;
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
    [ObservableProperty] private double _progress;
    [ObservableProperty] private PomodoroPhase _phase;
    [ObservableProperty] private bool _isBreakInterval;
    [ObservableProperty] private TimerAlert _alert;
    [ObservableProperty] private bool _isWorkEndedAlert;
    [ObservableProperty] private bool _isBreakEndedAlert;
    [ObservableProperty] private bool _isAlerting;

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
    private void HideToTray() => _navigator.HideTimer();

    [RelayCommand]
    private void Quit() => _navigator.Quit();

    // ── Refresh ──────────────────────────────────────────────────────────────

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Time properties change every second; the counter can hit the database,
        // so only the time-dependent parts are recomputed on ticks.
        if (e.PropertyName is nameof(IPomodoroEngine.DisplayValue))
        {
            Progress = _engine.Progress;
            UpdateTime(_engine.DisplayValue);
            UpdateSummary();
        }
        else if (e.PropertyName is not (nameof(IPomodoroEngine.Elapsed) or nameof(IPomodoroEngine.Remaining) or nameof(IPomodoroEngine.Progress)))
        {
            Refresh();
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

    public void Refresh()
    {
        var phase = _engine.Phase;
        var paused = phase == PomodoroPhase.Pause;
        var pausedBreak = paused && _engine.PausedPhase == PomodoroPhase.Break;
        var inBreak = phase is PomodoroPhase.Break or PomodoroPhase.BreakEnded || pausedBreak;

        Phase = phase;
        IsBreakInterval = inBreak;
        Progress = _engine.Progress;
        // The daily goal only means something next to today's count; "3/12" beside the
        // position in the cycle or a since-launch count would read as progress it isn't.
        var counter = _engine.Counter;
        var goal = _engineSettings.Counter == CounterEnum.CompletedToday ? _general.DailyGoal : 0;
        CounterText = goal > 0 ? $"{counter}/{goal}" : counter.ToString();
        CounterTooltip = _engineSettings.Counter switch
        {
            CounterEnum.CompletedToday when goal > 0 => $"{counter} of {goal} pomodoros done today",
            CounterEnum.CompletedToday => "Pomodoros completed today",
            CounterEnum.CompletedThisSession => "Pomodoros completed since YAPA started",
            _ => $"Pomodoro {_engine.Index} of {Math.Max(1, _engineSettings.PomodorosBeforeLongBreak)} before the long break",
        };

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
        CanLongBreak = _engine.CanStartLongBreak;
        LongBreakMenuText = phase is PomodoroPhase.Break || pausedBreak ? "Make this a long break" : "Take long break now";

        var flashing = !_theme.DisableFlashingAnimation;
        Alert = phase switch
        {
            PomodoroPhase.WorkEnded => TimerAlert.WorkEnded,
            PomodoroPhase.BreakEnded => TimerAlert.BreakEnded,
            _ => TimerAlert.None,
        };
        IsWorkEndedAlert = flashing && Alert == TimerAlert.WorkEnded;
        IsBreakEndedAlert = flashing && Alert == TimerAlert.BreakEnded;
        IsAlerting = IsWorkEndedAlert || IsBreakEndedAlert;

        UpdateSummary();

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        LongBreakCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTime(int totalSeconds)
    {
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        ShowSeconds = !_theme.HideSeconds;

        Minutes = minutes.ToString("00");
        Seconds = seconds.ToString("00");

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
}
