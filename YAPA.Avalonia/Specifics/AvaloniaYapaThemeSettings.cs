using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaYapaThemeSettings : IPluginSettings, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly ISettingsForComponent _s;

    public AvaloniaYapaThemeSettings(ISettings settings)
        => _s = settings.GetSettingsForComponent("YapaTheme");

    public int Width
    {
        get => _s.Get(nameof(Width), 200);
        set { _s.Update(nameof(Width), value); Notify(); }
    }

    public double ClockOpacity
    {
        get => _s.Get(nameof(ClockOpacity), 1.0);
        set { _s.Update(nameof(ClockOpacity), value); Notify(); }
    }

    public double ShadowOpacity
    {
        get => _s.Get(nameof(ShadowOpacity), 0.6);
        set { _s.Update(nameof(ShadowOpacity), value); Notify(); }
    }

    public Color TextColor
    {
        get => ParseColor(_s.Get(nameof(TextColor), "White"), Colors.White);
        set { _s.Update(nameof(TextColor), value.ToString()); Notify(); }
    }

    public Color ShadowColor
    {
        get => ParseColor(_s.Get(nameof(ShadowColor), "Black"), Colors.Black);
        set { _s.Update(nameof(ShadowColor), value.ToString()); Notify(); }
    }

    public bool DisableFlashingAnimation
    {
        get => _s.Get(nameof(DisableFlashingAnimation), false);
        set { _s.Update(nameof(DisableFlashingAnimation), value); Notify(); }
    }

    public bool ShowStatusText
    {
        get => _s.Get(nameof(ShowStatusText), true);
        set { _s.Update(nameof(ShowStatusText), value); Notify(); }
    }

    public bool HideSeconds
    {
        get => _s.Get(nameof(HideSeconds), false);
        set { _s.Update(nameof(HideSeconds), value); Notify(); }
    }

    public bool HideButtons
    {
        get => _s.Get(nameof(HideButtons), false);
        set { _s.Update(nameof(HideButtons), value); Notify(); }
    }

    public bool MinimizeToTray
    {
        get => _s.Get(nameof(MinimizeToTray), true);
        set { _s.Update(nameof(MinimizeToTray), value); Notify(); }
    }

    public int DigitCellWidth
    {
        get => _s.Get(nameof(DigitCellWidth), 36);
        set { _s.Update(nameof(DigitCellWidth), value); Notify(); }
    }

    public void DeferChanges() => _s.DeferChanges();

    public void ResetToDefaults()
    {
        Width                    = 200;
        ClockOpacity             = 1.0;
        ShadowOpacity            = 0.6;
        TextColor                = Colors.White;
        ShadowColor              = Colors.Black;
        DisableFlashingAnimation = false;
        ShowStatusText           = true;
        HideSeconds              = false;
        HideButtons              = false;
        MinimizeToTray           = true;
        DigitCellWidth           = 36;
    }

    private static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try { return Color.Parse(s); }
        catch { return fallback; }
    }
}
