using System.ComponentModel;
using System.Runtime.CompilerServices;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>App behaviour that isn't tied to a timer theme.</summary>
public sealed class GeneralSettings : IPluginSettings, INotifyPropertyChanged
{
    private readonly ISettingsForComponent _s;

    public GeneralSettings(ISettings settings) => _s = settings.GetSettingsForComponent("General");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(T value, [CallerMemberName] string name = "")
    {
        _s.Update(name, value!);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public bool AlwaysOnTop
    {
        get => _s.Get(nameof(AlwaysOnTop), true);
        set => Set(value);
    }

    public bool ShowOnAllWorkspaces
    {
        get => _s.Get(nameof(ShowOnAllWorkspaces), true);
        set => Set(value);
    }

    /// <summary>Keep the timer visible when "Show Desktop" (Super+D) hides everything else.</summary>
    public bool StayOnShowDesktop
    {
        get => _s.Get(nameof(StayOnShowDesktop), true);
        set => Set(value);
    }

    /// <summary>Whether the timer appears in the dock / Alt+Tab.</summary>
    public bool ShowInTaskbar
    {
        get => _s.Get(nameof(ShowInTaskbar), false);
        set => Set(value);
    }

    /// <summary>On X11/GNOME, keep the timer out of areas where the shell swallows clicks (dock, top bar).</summary>
    public bool AvoidShellPanels
    {
        get => _s.Get(nameof(AvoidShellPanels), true);
        set => Set(value);
    }

    public bool StartHidden
    {
        get => _s.Get(nameof(StartHidden), false);
        set => Set(value);
    }

    public bool PauseWhenLocked
    {
        get => _s.Get(nameof(PauseWhenLocked), false);
        set => Set(value);
    }

    public bool DesktopNotifications
    {
        get => _s.Get(nameof(DesktopNotifications), true);
        set => Set(value);
    }

    /// <summary>Daily target shown on the statistics page; 0 hides it.</summary>
    public int DailyGoal
    {
        get => _s.Get(nameof(DailyGoal), 8);
        set => Set(value);
    }

    /// <summary>The time of day the goal would be reached, on the timer beside the progress line.</summary>
    public bool ShowGoalForecast
    {
        get => _s.Get(nameof(ShowGoalForecast), true);
        set => Set(value);
    }

    public void ResetToDefaults()
    {
        AlwaysOnTop = true;
        ShowOnAllWorkspaces = true;
        StayOnShowDesktop = true;
        ShowInTaskbar = false;
        AvoidShellPanels = true;
        StartHidden = false;
        PauseWhenLocked = false;
        DesktopNotifications = true;
        DailyGoal = 8;
        ShowGoalForecast = true;
    }

    public void DeferChanges() => _s.DeferChanges();
}
