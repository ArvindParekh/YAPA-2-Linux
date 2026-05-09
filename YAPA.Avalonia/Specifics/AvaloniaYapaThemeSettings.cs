using Avalonia.Media;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaYapaThemeSettings : IPluginSettings
{
    private readonly ISettingsForComponent _s;

    public AvaloniaYapaThemeSettings(ISettings settings)
        => _s = settings.GetSettingsForComponent("YapaTheme");

    public int Width
    {
        get => _s.Get(nameof(Width), 200);
        set => _s.Update(nameof(Width), value);
    }

    public double ClockOpacity
    {
        get => _s.Get(nameof(ClockOpacity), 1.0);
        set => _s.Update(nameof(ClockOpacity), value);
    }

    public double ShadowOpacity
    {
        get => _s.Get(nameof(ShadowOpacity), 0.6);
        set => _s.Update(nameof(ShadowOpacity), value);
    }

    public Color TextColor
    {
        get => ParseColor(_s.Get(nameof(TextColor), "White"), Colors.White);
        set => _s.Update(nameof(TextColor), value.ToString());
    }

    public Color ShadowColor
    {
        get => ParseColor(_s.Get(nameof(ShadowColor), "Black"), Colors.Black);
        set => _s.Update(nameof(ShadowColor), value.ToString());
    }

    public bool DisableFlashingAnimation
    {
        get => _s.Get(nameof(DisableFlashingAnimation), false);
        set => _s.Update(nameof(DisableFlashingAnimation), value);
    }

    public bool ShowStatusText
    {
        get => _s.Get(nameof(ShowStatusText), true);
        set => _s.Update(nameof(ShowStatusText), value);
    }

    public bool HideSeconds
    {
        get => _s.Get(nameof(HideSeconds), false);
        set => _s.Update(nameof(HideSeconds), value);
    }

    public bool HideButtons
    {
        get => _s.Get(nameof(HideButtons), false);
        set => _s.Update(nameof(HideButtons), value);
    }

    public bool MinimizeToTray
    {
        get => _s.Get(nameof(MinimizeToTray), true);
        set => _s.Update(nameof(MinimizeToTray), value);
    }

    public void DeferChanges() => _s.DeferChanges();

    private static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try { return Color.Parse(s); }
        catch { return fallback; }
    }
}
