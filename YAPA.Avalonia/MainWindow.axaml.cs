using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using YAPA.Avalonia.Windows;

namespace YAPA.Avalonia;

/// <summary>The classic "YAPA 1.0" timer.</summary>
public partial class MainWindow : TimerWindowBase
{
    private const double AspectRatio = 120.0 / 260.0;
    private const int MinimumWidth = 120;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override string PositionKey => "MainWindow";

    protected override void OnAppearanceChanged()
    {
        var width = Math.Max(MinimumWidth, Appearance.Width);
        Width = width;
        Height = Math.Round(width * AspectRatio);

        StatusLabel.IsVisible = Appearance.ShowStatusText;
        TimeLabel.FontFamily = DigitFamily;
        TimeLabel.FontWeight = Appearance.DigitFontWeight;
        Backdrop.IsVisible = Appearance.Backdrop;
        Backdrop.Background = new SolidColorBrush(Appearance.BackdropColor, Math.Clamp(Appearance.BackdropOpacity, 0, 1));
        ProgressLine.IsVisible = Appearance.ShowProgress;

        TimeLabel.Effect = Appearance.ShadowOpacity > 0
            ? new DropShadowEffect
            {
                Color = Appearance.ShadowColor,
                Opacity = Appearance.ShadowOpacity,
                BlurRadius = 6,
                OffsetX = 1.5,
                OffsetY = 1.5,
            }
            : null;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => MinimizeOrHide();

    private void OnMenuClick(object? sender, RoutedEventArgs e) => OpenActionsMenu(MenuButton);

    protected override Control MenuAnchor => MenuButton;
}
