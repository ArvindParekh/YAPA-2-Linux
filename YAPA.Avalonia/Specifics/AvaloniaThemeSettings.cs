using System.ComponentModel;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaThemeSettings : IPluginSettings, INotifyPropertyChanged
{
    private readonly ISettingsForComponent _s;

    public AvaloniaThemeSettings(ISettings settings)
    {
        _s = settings.GetSettingsForComponent("ThemeManager");
        // Before the redesign the classic timer was the default, and profiles from then hold it
        // as their choice whether or not anyone chose it. Moved to Studio once; a theme picked
        // after that sticks.
        if (!_s.Get(StudioIntroduced, false))
        {
            if (_s.Get(nameof(ActiveTheme), "") == YAPA.Avalonia.Services.AppNavigator.ClassicTheme)
                _s.Update(nameof(ActiveTheme), YAPA.Avalonia.Services.AppNavigator.StudioTheme);
            _s.Update(StudioIntroduced, true);
        }
    }

    private const string StudioIntroduced = "StudioIntroduced";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ActiveTheme
    {
        get => _s.Get(nameof(ActiveTheme), YAPA.Avalonia.Services.AppNavigator.StudioTheme);
        set
        {
            if (value == ActiveTheme) return;
            _s.Update(nameof(ActiveTheme), value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveTheme)));
        }
    }

    public void ResetToDefaults() => ActiveTheme = YAPA.Avalonia.Services.AppNavigator.StudioTheme;

    public void DeferChanges() => _s.DeferChanges();
}
