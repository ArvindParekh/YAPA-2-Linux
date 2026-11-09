using Avalonia.Controls;
using YAPA.Avalonia.Settings.Pages;

namespace YAPA.Avalonia.Windows;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow()
    {
        InitializeComponent();
        Host.Content = new DashboardPage();
    }
}
