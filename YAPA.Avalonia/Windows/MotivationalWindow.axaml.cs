using System.ComponentModel;
using Avalonia.Interactivity;
using Avalonia.Media;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Windows;

/// <summary>A larger card with a quote that changes with every phase.</summary>
public partial class MotivationalWindow : TimerWindowBase
{
    public MotivationalWindow()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        ShowNextQuote();
    }

    protected override string PositionKey => "MotivationalWindow";

    protected override void OnAppearanceChanged()
    {
        StatusLabel.IsVisible = Theme.ShowStatusText;
        ProgressLine.IsVisible = Theme.ShowProgress;
        TimeLabel.Effect = Theme.ShadowOpacity > 0
            ? new DropShadowEffect { Color = Theme.ShadowColor, Opacity = Theme.ShadowOpacity, BlurRadius = 6, OffsetX = 1, OffsetY = 1 }
            : null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimerViewModel.Phase) && ViewModel.Phase is PomodoroPhase.Work or PomodoroPhase.Break)
            ShowNextQuote();
    }

    private void ShowNextQuote()
    {
        var quote = Quotes.Random();
        QuoteText.Text = quote.Text;
        QuoteSource.Text = string.IsNullOrEmpty(quote.Source) ? string.Empty : $"— {quote.Source}";
    }

    protected override void OnClosed(System.EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => MinimizeOrHide();
}
