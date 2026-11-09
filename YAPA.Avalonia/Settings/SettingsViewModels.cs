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
    public abstract SettingsPage Page { get; }
    public virtual bool CanReset => true;

    public void Reset()
    {
        ResetToDefaults();
        OnPropertyChanged(string.Empty); // everything on the page may have changed
    }

    protected abstract void ResetToDefaults();

    protected void Changed([CallerMemberName] string? name = null) => OnPropertyChanged(name);
}

public sealed class TimerPageViewModel(PomodoroEngineSettings s) : SettingsPageViewModel
{
    public override string Title => "Timer";
    public override SettingsPage Page => SettingsPage.Timer;

    // NumericUpDown binds to decimal?; a cleared box comes through as null and is ignored.
    public decimal? WorkMinutes { get => s.WorkTime / 60; set { if (value is { } v) { s.WorkTime = (int)v * 60; Changed(); } } }
    public decimal? BreakMinutes { get => s.BreakTime / 60; set { if (value is { } v) { s.BreakTime = (int)v * 60; Changed(); } } }
    public decimal? LongBreakMinutes { get => s.LongBreakTime / 60; set { if (value is { } v) { s.LongBreakTime = (int)v * 60; Changed(); } } }
    public decimal? PomodorosBeforeLongBreak { get => s.PomodorosBeforeLongBreak; set { if (value is { } v) { s.PomodorosBeforeLongBreak = (int)v; Changed(); } } }

    public bool AutoStartBreak { get => s.AutoStartBreak; set { s.AutoStartBreak = value; Changed(); } }
    public bool AutoStartWork { get => s.AutoStartWork; set { s.AutoStartWork = value; Changed(); } }
    public bool CountBackwards { get => s.CountBackwards; set { s.CountBackwards = value; Changed(); } }

    public IReadOnlyList<string> CounterOptions { get; } = ["Position in the cycle", "Completed today", "Completed since launch"];

    public int CounterIndex
    {
        get => (int)s.Counter;
        set { if (value >= 0) { s.Counter = (CounterEnum)value; Changed(); } }
    }

    protected override void ResetToDefaults() => s.ResetToDefaults();
}

public sealed class AppearancePageViewModel(AvaloniaYapaThemeSettings s, AvaloniaThemeSettings themes) : SettingsPageViewModel
{
    public override string Title => "Appearance";
    public override SettingsPage Page => SettingsPage.Appearance;

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

    public double Size { get => s.Width; set { s.Width = (int)Math.Round(value); Changed(); } }
    public double ClockOpacity { get => s.ClockOpacity; set { s.ClockOpacity = Math.Round(value, 2); Changed(); } }
    public double ShadowOpacity { get => s.ShadowOpacity; set { s.ShadowOpacity = Math.Round(value, 2); Changed(); } }

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
    public override SettingsPage Page => SettingsPage.General;

    public bool AlwaysOnTop { get => g.AlwaysOnTop; set { g.AlwaysOnTop = value; Changed(); } }
    public bool ShowOnAllWorkspaces { get => g.ShowOnAllWorkspaces; set { g.ShowOnAllWorkspaces = value; Changed(); } }
    public bool StayOnShowDesktop { get => g.StayOnShowDesktop; set { g.StayOnShowDesktop = value; Changed(); } }
    public bool ShowInTaskbar { get => g.ShowInTaskbar; set { g.ShowInTaskbar = value; Changed(); } }
    public bool AvoidShellPanels { get => g.AvoidShellPanels; set { g.AvoidShellPanels = value; Changed(); } }
    public bool MinimizeToTray { get => theme.MinimizeToTray; set { theme.MinimizeToTray = value; Changed(); } }
    public bool PauseWhenLocked { get => g.PauseWhenLocked; set { g.PauseWhenLocked = value; Changed(); } }
    public bool DesktopNotifications { get => g.DesktopNotifications; set { g.DesktopNotifications = value; Changed(); } }
    public decimal? DailyGoal { get => g.DailyGoal; set { if (value is { } v) { g.DailyGoal = (int)v; Changed(); } } }

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
        g.ResetToDefaults();
        theme.MinimizeToTray = true;
    }
}

public sealed partial class AboutPageViewModel : SettingsPageViewModel
{
    public override string Title => "About";
    public override SettingsPage Page => SettingsPage.About;
    public override bool CanReset => false;
    protected override void ResetToDefaults() { }

    public string Version => $"Version {Program.Version}";
    public string ConfigDirectory => AppPaths.ConfigDirectory;
    public string DataDirectory => AppPaths.DataDirectory;
    public string LogFile => Diagnostics.Logging.LogFile;
    public string Usage => SingleInstance.CommandLine.Usage;

    public IReadOnlyList<(string Keys, string Action)> Shortcuts { get; } =
    [
        ("Space", "Start, pause or resume"),
        ("Esc", "Stop: abandon the pomodoro, or end the break"),
        ("S", "Skip the break"),
        ("L", "Take the long break now"),
        ("Ctrl+,", "Settings"),
        ("Ctrl+I", "Statistics"),
        ("Ctrl+H", "Hide to tray"),
        ("Ctrl+Q", "Quit"),
        ("Right-click", "All actions"),
    ];

    public string ShortcutText => string.Join("\n", Shortcuts.Select(s => $"{s.Keys,-12} {s.Action}"));

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
