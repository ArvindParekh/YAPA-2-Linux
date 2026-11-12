namespace YAPA.Avalonia.Services;

public enum SettingsPage
{
    Timer,
    Appearance,
    Sounds,
    Assistant,
    Notifications,
    General,
    About,
}

/// <summary>
/// App-level navigation, shared by the timer window, tray, CLI and notifications,
/// so none of them needs to know how windows are created or found.
/// </summary>
public interface IAppNavigator
{
    void ShowTimer();
    void HideTimer();
    void ToggleTimerVisibility();
    void ShowSettings(SettingsPage page = SettingsPage.Timer);
    void ShowStatistics();

    /// <summary>Opens the assistant beside the timer.</summary>
    void ShowAssistant();

    void Quit();
}
