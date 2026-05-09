using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaThemeSettings : IPluginSettings
{
    private readonly ISettingsForComponent _s;

    public AvaloniaThemeSettings(ISettings settings)
        => _s = settings.GetSettingsForComponent("ThemeManager");

    public string ActiveTheme
    {
        get => _s.Get(nameof(ActiveTheme), "YAPA 1.0");
        set => _s.Update(nameof(ActiveTheme), value);
    }

    public void DeferChanges() => _s.DeferChanges();

    public void ResetToDefaults() => ActiveTheme = "YAPA 1.0";
}
