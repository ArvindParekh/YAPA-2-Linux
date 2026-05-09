using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Settings.Pages;

public partial class ThemeSettingsPage : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly AvaloniaYapaThemeSettings _s;

    public ThemeSettingsPage() : this(App.Bootstrapper!.Resolve<AvaloniaYapaThemeSettings>()) { }

    public ThemeSettingsPage(AvaloniaYapaThemeSettings settings)
    {
        _s = settings;
        InitializeComponent();
        DataContext = this;
    }

    public new int Width
    {
        get => _s.Width;
        set { _s.Width = value; Notify(); }
    }

    public double ClockOpacity
    {
        get => _s.ClockOpacity;
        set { _s.ClockOpacity = value; Notify(); Notify(nameof(ClockOpacityDisplay)); }
    }

    public string ClockOpacityDisplay => $"{ClockOpacity:P0}";

    public string TextColorHex
    {
        get => _s.TextColor.ToString();
        set
        {
            try { _s.TextColor = global::Avalonia.Media.Color.Parse(value); Notify(); }
            catch { /* invalid hex — ignore */ }
        }
    }

    public string ShadowColorHex
    {
        get => _s.ShadowColor.ToString();
        set
        {
            try { _s.ShadowColor = global::Avalonia.Media.Color.Parse(value); Notify(); }
            catch { /* invalid hex — ignore */ }
        }
    }

    public bool DisableFlashingAnimation
    {
        get => _s.DisableFlashingAnimation;
        set { _s.DisableFlashingAnimation = value; Notify(); }
    }

    public bool ShowStatusText
    {
        get => _s.ShowStatusText;
        set { _s.ShowStatusText = value; Notify(); }
    }

    public bool HideSeconds
    {
        get => _s.HideSeconds;
        set { _s.HideSeconds = value; Notify(); }
    }

    public bool HideButtons
    {
        get => _s.HideButtons;
        set { _s.HideButtons = value; Notify(); }
    }
}
