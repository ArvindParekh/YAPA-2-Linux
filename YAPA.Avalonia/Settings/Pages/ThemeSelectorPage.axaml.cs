using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Settings.Pages;

public partial class ThemeSelectorPage : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly AvaloniaThemeSettings _s;

    public List<string> AvailableThemes { get; } = ["YAPA 1.0", "Motivational"];

    public string ActiveTheme
    {
        get => _s.ActiveTheme;
        set { _s.ActiveTheme = value; Notify(); }
    }

    public ThemeSelectorPage() : this(App.Bootstrapper!.Resolve<AvaloniaThemeSettings>()) { }

    public void NotifyAllChanged()
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    public ThemeSelectorPage(AvaloniaThemeSettings settings)
    {
        _s = settings;
        InitializeComponent();
        DataContext = this;
    }
}
