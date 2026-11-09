using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Appearance of the timer window. The component name "YapaTheme" and the key
/// names are kept from the original app so existing settings files still apply.
/// </summary>
public sealed class AvaloniaYapaThemeSettings : IPluginSettings, INotifyPropertyChanged
{
    public const int DefaultWidth = 200;

    private readonly ISettingsForComponent _s;

    public AvaloniaYapaThemeSettings(ISettings settings) => _s = settings.GetSettingsForComponent("YapaTheme");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(object value, [CallerMemberName] string name = "")
    {
        _s.Update(name, value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public int Width
    {
        get => _s.Get(nameof(Width), DefaultWidth);
        set => Set(value);
    }

    public double ClockOpacity
    {
        get => _s.Get(nameof(ClockOpacity), 1.0);
        set => Set(value);
    }

    public double ShadowOpacity
    {
        get => _s.Get(nameof(ShadowOpacity), 0.6);
        set => Set(value);
    }

    public Color TextColor
    {
        get => ParseColor(_s.Get(nameof(TextColor), "White"), Colors.White);
        set => Set(value.ToString());
    }

    public Color ShadowColor
    {
        get => ParseColor(_s.Get(nameof(ShadowColor), "Black"), Colors.Black);
        set => Set(value.ToString());
    }

    public bool DisableFlashingAnimation
    {
        get => _s.Get(nameof(DisableFlashingAnimation), false);
        set => Set(value);
    }

    public bool ShowStatusText
    {
        get => _s.Get(nameof(ShowStatusText), true);
        set => Set(value);
    }

    public bool ShowProgress
    {
        get => _s.Get(nameof(ShowProgress), true);
        set => Set(value);
    }

    public bool HideSeconds
    {
        get => _s.Get(nameof(HideSeconds), false);
        set => Set(value);
    }

    /// <summary>True: controls appear only while the pointer is over the timer.</summary>
    public bool HideButtons
    {
        get => _s.Get(nameof(HideButtons), false);
        set => Set(value);
    }

    public bool MinimizeToTray
    {
        get => _s.Get(nameof(MinimizeToTray), true);
        set => Set(value);
    }

    public void ResetToDefaults()
    {
        Width = DefaultWidth;
        ClockOpacity = 1.0;
        ShadowOpacity = 0.6;
        TextColor = Colors.White;
        ShadowColor = Colors.Black;
        DisableFlashingAnimation = false;
        ShowStatusText = true;
        ShowProgress = true;
        HideSeconds = false;
        HideButtons = false;
        MinimizeToTray = true;
    }

    public void DeferChanges() => _s.DeferChanges();

    private static Color ParseColor(string? s, Color fallback)
        => !string.IsNullOrWhiteSpace(s) && Color.TryParse(s, out var c) ? c : fallback;
}
