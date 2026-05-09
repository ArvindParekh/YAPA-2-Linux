using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Settings.Pages;

public partial class EngineSettingsPage : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly PomodoroEngineSettings _s;

    public EngineSettingsPage() : this(App.Bootstrapper!.Resolve<PomodoroEngineSettings>()) { }

    public EngineSettingsPage(PomodoroEngineSettings settings)
    {
        _s = settings;
        InitializeComponent();
        DataContext = this;
    }

    // ── Timing ────────────────────────────────────────────────────────────────

    // NumericUpDown.Value is decimal? — guard against null (e.g., when field is cleared)
    public decimal? WorkTimeMinutes
    {
        get => _s.WorkTime / 60m;
        set { if (value.HasValue) { _s.WorkTime = (int)value.Value * 60; Notify(); } }
    }

    public decimal? BreakTimeMinutes
    {
        get => _s.BreakTime / 60m;
        set { if (value.HasValue) { _s.BreakTime = (int)value.Value * 60; Notify(); } }
    }

    public decimal? LongBreakTimeMinutes
    {
        get => _s.LongBreakTime / 60m;
        set { if (value.HasValue) { _s.LongBreakTime = (int)value.Value * 60; Notify(); } }
    }

    public decimal? PomodorosBeforeLongBreak
    {
        get => _s.PomodorosBeforeLongBreak;
        set { if (value.HasValue) { _s.PomodorosBeforeLongBreak = (int)value.Value; Notify(); } }
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    public bool AutoStartBreak
    {
        get => _s.AutoStartBreak;
        set { _s.AutoStartBreak = value; Notify(); }
    }

    public bool AutoStartWork
    {
        get => _s.AutoStartWork;
        set { _s.AutoStartWork = value; Notify(); }
    }

    public bool CountBackwards
    {
        get => _s.CountBackwards;
        set { _s.CountBackwards = value; Notify(); }
    }

    // ── Counter ───────────────────────────────────────────────────────────────

    private static readonly string[] _counterLabels =
        ["Pomodoro index (today)", "Completed today", "Completed this session"];
    private static readonly CounterEnum[] _counterValues =
        [CounterEnum.PomodoroIndex, CounterEnum.CompletedToday, CounterEnum.CompletedThisSession];

    public List<string> CounterOptions { get; } = [.. _counterLabels];

    public string SelectedCounterDisplay
    {
        get
        {
            var idx = Array.IndexOf(_counterValues, _s.Counter);
            return idx >= 0 ? _counterLabels[idx] : _counterLabels[0];
        }
        set
        {
            var idx = Array.IndexOf(_counterLabels, value);
            if (idx >= 0) { _s.Counter = _counterValues[idx]; Notify(); }
        }
    }

    // ── Sound / volume ────────────────────────────────────────────────────────

    public bool DisableSoundNotifications
    {
        get => _s.DisableSoundNotifications;
        set { _s.DisableSoundNotifications = value; Notify(); }
    }

    public double Volume
    {
        get => _s.Volume;
        set { _s.Volume = value; Notify(); }
    }
}
