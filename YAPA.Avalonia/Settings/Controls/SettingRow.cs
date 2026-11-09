using Avalonia;
using Avalonia.Controls;

namespace YAPA.Avalonia.Settings.Controls;

/// <summary>A settings line: title and explanation on the left, the control on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}
