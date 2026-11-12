using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Settings;

/// <summary>
/// Settings pages read and write the settings objects directly, and every
/// change applies at once; there is no Save. Each page exposes Reset to put
/// its own values back to defaults.
/// </summary>
public abstract class SettingsPageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>One line under the title: what the page is for.</summary>
    public abstract string Subtitle { get; }

    public abstract SettingsPage Page { get; }
    public virtual bool CanReset => true;

    public global::Avalonia.Media.Geometry Icon => SettingsIcons.For(Page);

    public void Reset()
    {
        ResetToDefaults();
        OnPropertyChanged(string.Empty); // everything on the page may have changed
    }

    protected abstract void ResetToDefaults();

    /// <summary>Re-reads every value; they may have been changed from outside the page.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    protected void Changed([CallerMemberName] string? name = null) => OnPropertyChanged(name);
}

public sealed class TimerPageViewModel(PomodoroEngineSettings s, GeneralSettings g) : SettingsPageViewModel
{
    public override string Title => "Timer";
    public override string Subtitle => "How long you work and rest, and what happens in between.";
    public override SettingsPage Page => SettingsPage.Timer;

    /// <summary>"One cycle · 3 h 50 min of which 3 h 20 min focused".</summary>
    public string CycleSummary
    {
        get
        {
            var count = Math.Max(1, s.PomodorosBeforeLongBreak);
            var work = s.WorkTime / 60 * count;
            var total = work + s.BreakTime / 60 * (count - 1) + s.LongBreakTime / 60;
            return $"One cycle takes {Duration(total)}: {count} × {s.WorkTime / 60} min of focus, then the long break";
        }
    }

    private static string Duration(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    public double StripWork => s.WorkTime / 60.0;
    public double StripShortBreak => s.BreakTime / 60.0;
    public double StripLongBreak => s.LongBreakTime / 60.0;
    public int StripCount => Math.Max(1, s.PomodorosBeforeLongBreak);

    public decimal? DailyGoal { get => g.DailyGoal; set { if (value is { } v) { g.DailyGoal = (int)v; Changed(); } } }
    public bool ShowGoalForecast { get => g.ShowGoalForecast; set { g.ShowGoalForecast = value; Changed(); } }

    // NumericUpDown binds to decimal?; a cleared box comes through as null and is ignored.
    public decimal? WorkMinutes { get => s.WorkTime / 60; set { if (value is { } v) { s.WorkTime = (int)v * 60; Changed(); Shape(); } } }
    public decimal? BreakMinutes { get => s.BreakTime / 60; set { if (value is { } v) { s.BreakTime = (int)v * 60; Changed(); Shape(); } } }
    public decimal? LongBreakMinutes { get => s.LongBreakTime / 60; set { if (value is { } v) { s.LongBreakTime = (int)v * 60; Changed(); Shape(); } } }
    public decimal? PomodorosBeforeLongBreak { get => s.PomodorosBeforeLongBreak; set { if (value is { } v) { s.PomodorosBeforeLongBreak = (int)v; Changed(); Shape(); } } }

    /// <summary>The cycle strip and its line follow every length.</summary>
    private void Shape()
    {
        Changed(nameof(CycleSummary));
        Changed(nameof(StripWork));
        Changed(nameof(StripShortBreak));
        Changed(nameof(StripLongBreak));
        Changed(nameof(StripCount));
    }

    public bool AutoStartBreak { get => s.AutoStartBreak; set { s.AutoStartBreak = value; Changed(); } }
    public bool AutoStartWork { get => s.AutoStartWork; set { s.AutoStartWork = value; Changed(); } }
    public bool CountBackwards { get => s.CountBackwards; set { s.CountBackwards = value; Changed(); } }

    public IReadOnlyList<string> CounterOptions { get; } = ["Position in the cycle", "Completed today", "Completed since launch"];

    public int CounterIndex
    {
        get => (int)s.Counter;
        set { if (value >= 0) { s.Counter = (CounterEnum)value; Changed(); } }
    }

    protected override void ResetToDefaults()
    {
        s.ResetToDefaults();
        g.DailyGoal = 8;
        g.ShowGoalForecast = true;
    }
}

public sealed class AppearancePageViewModel(AvaloniaYapaThemeSettings s, AvaloniaThemeSettings themes) : SettingsPageViewModel
{
    public override string Title => "Appearance";
    public override string Subtitle => "How the timer looks on your desktop.";
    public override SettingsPage Page => SettingsPage.Appearance;

    // ── Live preview ─────────────────────────────────────────────────────
    public IBrush PreviewText => new SolidColorBrush(s.TextColor);
    public Color PreviewShadow => s.ShadowColor;
    public double PreviewShadowOpacity => s.ShadowOpacity;
    public double PreviewOpacity => Math.Clamp(s.ClockOpacity, 0.2, 1);
    public string PreviewTime => s.HideSeconds ? "24" : "24:59";
    /// <summary>The preview grows and shrinks with the size slider, within its frame.</summary>
    public double PreviewDigits => Math.Clamp(s.Width / 200.0 * 58, 34, 76);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(PreviewText) or nameof(PreviewShadow) or nameof(PreviewShadowOpacity) or nameof(PreviewOpacity)
            or nameof(PreviewTime) or nameof(PreviewDigits)) return;
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewShadow));
        OnPropertyChanged(nameof(PreviewShadowOpacity));
        OnPropertyChanged(nameof(PreviewOpacity));
        OnPropertyChanged(nameof(PreviewTime));
        OnPropertyChanged(nameof(PreviewDigits));
    }

    public string SizeText => $"{s.Width} px";
    public string OpacityText => $"{s.ClockOpacity:P0}";
    public string ShadowText => $"{s.ShadowOpacity:P0}";

    public IReadOnlyList<string> Themes { get; } = ["Classic", "Motivational"];

    public int ThemeIndex
    {
        get => themes.ActiveTheme == AppNavigator.MotivationalTheme ? 1 : 0;
        set { themes.ActiveTheme = value == 1 ? AppNavigator.MotivationalTheme : AppNavigator.ClassicTheme; Changed(); }
    }

    public IReadOnlyList<string> TextColors { get; } =
        ["#FFFFFFFF", "#FFFF6347", "#FFFF0000", "#FFFFA500", "#FFFFD700", "#FF7CFC00", "#FF00CED1", "#FF1E90FF", "#FFEE82EE", "#FF000000"];

    public IReadOnlyList<string> ShadowColors { get; } = ["#FF000000", "#FF404040", "#FFFFFFFF"];

    public string? TextColor
    {
        get => Match(TextColors, s.TextColor);
        set { if (value != null && Color.TryParse(value, out var c)) { s.TextColor = c; Changed(); Changed(nameof(TextColorHex)); } }
    }

    public string TextColorHex
    {
        get => s.TextColor.ToString();
        set { if (Color.TryParse(value, out var c)) { s.TextColor = c; Changed(); Changed(nameof(TextColor)); } }
    }

    public string? ShadowColor
    {
        get => Match(ShadowColors, s.ShadowColor);
        set { if (value != null && Color.TryParse(value, out var c)) { s.ShadowColor = c; Changed(); } }
    }

    public double Size { get => s.Width; set { s.Width = (int)Math.Round(value); Changed(); Changed(nameof(SizeText)); } }
    public double ClockOpacity { get => s.ClockOpacity; set { s.ClockOpacity = Math.Round(value, 2); Changed(); Changed(nameof(OpacityText)); } }
    public double ShadowOpacity { get => s.ShadowOpacity; set { s.ShadowOpacity = Math.Round(value, 2); Changed(); Changed(nameof(ShadowText)); } }

    public bool ShowSeconds { get => !s.HideSeconds; set { s.HideSeconds = !value; Changed(); } }
    public bool ShowStatusText { get => s.ShowStatusText; set { s.ShowStatusText = value; Changed(); } }
    public bool ShowProgress { get => s.ShowProgress; set { s.ShowProgress = value; Changed(); } }
    public bool ControlsOnHover { get => s.HideButtons; set { s.HideButtons = value; Changed(); } }
    public bool FlashWhenDone { get => !s.DisableFlashingAnimation; set { s.DisableFlashingAnimation = !value; Changed(); } }

    protected override void ResetToDefaults()
    {
        s.ResetToDefaults();
        themes.ResetToDefaults();
    }

    private static string? Match(IReadOnlyList<string> palette, Color color)
        => palette.FirstOrDefault(p => Color.Parse(p) == color);
}

public sealed class GeneralPageViewModel(GeneralSettings g, AvaloniaYapaThemeSettings theme) : SettingsPageViewModel
{
    public override string Title => "General";
    public override string Subtitle => "Window behaviour, startup and notifications.";
    public override SettingsPage Page => SettingsPage.General;

    public bool AlwaysOnTop { get => g.AlwaysOnTop; set { g.AlwaysOnTop = value; Changed(); } }
    public bool ShowOnAllWorkspaces { get => g.ShowOnAllWorkspaces; set { g.ShowOnAllWorkspaces = value; Changed(); } }
    public bool StayOnShowDesktop { get => g.StayOnShowDesktop; set { g.StayOnShowDesktop = value; Changed(); } }
    public bool ShowInTaskbar { get => g.ShowInTaskbar; set { g.ShowInTaskbar = value; Changed(); } }
    public bool AvoidShellPanels { get => g.AvoidShellPanels; set { g.AvoidShellPanels = value; Changed(); } }
    public bool MinimizeToTray { get => theme.MinimizeToTray; set { theme.MinimizeToTray = value; Changed(); } }
    public bool PauseWhenLocked { get => g.PauseWhenLocked; set { g.PauseWhenLocked = value; Changed(); } }
    public bool DesktopNotifications { get => g.DesktopNotifications; set { g.DesktopNotifications = value; Changed(); } }
    public bool AutostartSupported => Autostart.IsSupported;

    public bool LaunchAtLogin
    {
        get => Autostart.IsEnabled;
        set { Autostart.SetEnabled(value, g.StartHidden); Changed(); }
    }

    public bool StartHidden
    {
        get => g.StartHidden;
        set
        {
            g.StartHidden = value;
            if (Autostart.IsEnabled) Autostart.SetEnabled(true, value);
            Changed();
        }
    }

    protected override void ResetToDefaults()
    {
        // Not the goal: it lives on the Timer page now, and resets there.
        var goal = g.DailyGoal;
        var forecast = g.ShowGoalForecast;
        g.ResetToDefaults();
        g.DailyGoal = goal;
        g.ShowGoalForecast = forecast;
        theme.MinimizeToTray = true;
    }
}

public sealed record Shortcut(string Keys, string Action)
{
    public IReadOnlyList<string> Caps => Keys.Split(' ');
}

public sealed partial class AboutPageViewModel : SettingsPageViewModel
{
    public override string Title => "About";
    public override string Subtitle => "Yet Another Pomodoro App, rebuilt for Linux.";
    public override SettingsPage Page => SettingsPage.About;
    public override bool CanReset => false;
    protected override void ResetToDefaults() { }

    public string Version => $"Version {Program.Version}";
    public string ConfigDirectory => AppPaths.ConfigDirectory;
    public string DataDirectory => AppPaths.DataDirectory;
    public string LogFile => Diagnostics.Logging.LogFile;
    public string Usage => SingleInstance.CommandLine.Usage;

    public IReadOnlyList<Shortcut> Shortcuts { get; } =
    [
        new("Space", "Start, pause or resume"),
        new("Esc", "Stop: abandon the pomodoro, or end the break"),
        new("S", "Skip the break"),
        new("L", "Take the long break now"),
        new("/", "Ask YAPA (or double-click the timer)"),
        new("Ctrl ,", "Settings"),
        new("Ctrl I", "Statistics"),
        new("Ctrl H", "Hide to tray"),
        new("Ctrl Q", "Quit"),
        new("Right-click", "Settings; ⋯ has every action"),
    ];

    [RelayCommand]
    private static void Open(string path) => OpenPath(path);

    public static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"[about] xdg-open {path} failed: {e.Message}");
        }
    }
}
