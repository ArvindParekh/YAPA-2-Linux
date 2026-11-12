using System;
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

    // ── Looks: the digits, the progress line and an optional plate behind the timer ──

    public static readonly string[] DigitWeights = ["thin", "light", "regular", "medium", "bold"];
    public static readonly string[] DigitFonts = ["inter", "geist"];
    public static readonly Color DefaultBreakColor = Color.Parse("#FF3CB371");
    public static readonly Color DefaultBackdropColor = Color.Parse("#FF15151A");

    public string DigitWeight
    {
        get => _s.Get(nameof(DigitWeight), "light") is { } w && Array.IndexOf(DigitWeights, w) >= 0 ? w : "light";
        set => Set(value);
    }

    public string DigitFont
    {
        get => _s.Get(nameof(DigitFont), "inter") is { } f && Array.IndexOf(DigitFonts, f) >= 0 ? f : "inter";
        set => Set(value);
    }

    /// <summary>The progress line during work; null follows the text colour.</summary>
    public Color? ProgressColor
    {
        get => _s.Get<string?>(nameof(ProgressColor), null) is { Length: > 0 } c && Color.TryParse(c, out var parsed) ? parsed : null;
        set => Set(value?.ToString() ?? "");
    }

    public Color BreakColor
    {
        get => ParseColor(_s.Get<string?>(nameof(BreakColor), null), DefaultBreakColor);
        set => Set(value.ToString());
    }

    /// <summary>A rounded plate behind the timer, for wallpapers where floating digits get lost.</summary>
    public bool Backdrop
    {
        get => _s.Get(nameof(Backdrop), false);
        set => Set(value);
    }

    public Color BackdropColor
    {
        get => ParseColor(_s.Get<string?>(nameof(BackdropColor), null), DefaultBackdropColor);
        set => Set(value.ToString());
    }

    public double BackdropOpacity
    {
        get => _s.Get(nameof(BackdropOpacity), 0.55);
        set => Set(value);
    }

    public FontWeight DigitFontWeight => DigitWeight switch
    {
        "thin" => FontWeight.Thin,
        "regular" => FontWeight.Regular,
        "medium" => FontWeight.Medium,
        "bold" => FontWeight.Bold,
        _ => FontWeight.Light,
    };

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
        DigitWeight = "light";
        DigitFont = "inter";
        ProgressColor = null;
        BreakColor = DefaultBreakColor;
        Backdrop = false;
        BackdropColor = DefaultBackdropColor;
        BackdropOpacity = 0.55;
    }

    public void DeferChanges() => _s.DeferChanges();

    private static Color ParseColor(string? s, Color fallback)
        => !string.IsNullOrWhiteSpace(s) && Color.TryParse(s, out var c) ? c : fallback;
}
