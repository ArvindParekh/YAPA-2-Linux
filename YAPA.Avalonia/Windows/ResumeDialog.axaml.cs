using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YAPA.Avalonia.Windows;

public partial class ResumeDialog : Window
{
    public ResumeDialog() : this(TimeSpan.Zero) { }

    public ResumeDialog(TimeSpan remaining)
    {
        InitializeComponent();
        MessageText.Text = $"Remaining: {remaining.Minutes:00}:{remaining.Seconds:00}. Resume pomodoro?";
    }

    private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);
    private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);
}
