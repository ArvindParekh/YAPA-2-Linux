using System.ComponentModel;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaThemeSettings : IPluginSettings, INotifyPropertyChanged
{
    private readonly ISettingsForComponent _s;

    public AvaloniaThemeSettings(ISettings settings) => _s = settings.GetSettingsForComponent("ThemeManager");

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
