using Avalonia.Controls;
using System.ComponentModel;
using Avalonia.Interactivity;
using Avalonia.Media;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Windows;

/// <summary>A larger card with a quote that changes with every phase.</summary>
public partial class MotivationalWindow : TimerWindowBase
{
    private readonly YAPA.Avalonia.Specifics.TimerTexts _texts;

    public MotivationalWindow()
    {
        InitializeComponent();
        _texts = App.Bootstrapper!.Resolve<YAPA.Avalonia.Specifics.TimerTexts>();
        _texts.PropertyChanged += OnTextsChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ShowNextQuote();
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(YAPA.Avalonia.Specifics.TimerTexts.Quote)) ShowNextQuote();
    }

    protected override string PositionKey => "MotivationalWindow";

    protected override void OnAppearanceChanged()
    {
        StatusLabel.IsVisible = Appearance.ShowStatusText;
        ProgressLine.IsVisible = Appearance.ShowProgress;
        TimeLabel.FontFamily = DigitFamily;
        TimeLabel.FontWeight = Appearance.DigitFontWeight;
        // The card is this theme's backdrop; a look's backdrop colour recolours it.
        Card.Background = Appearance.Backdrop
            ? new SolidColorBrush(Appearance.BackdropColor, System.Math.Clamp(Appearance.BackdropOpacity, 0, 1))
            : new SolidColorBrush(Color.Parse("#E61A1A2E"));
        TimeLabel.Effect = Appearance.ShadowOpacity > 0
            ? new DropShadowEffect { Color = Appearance.ShadowColor, Opacity = Appearance.ShadowOpacity, BlurRadius = 6, OffsetX = 1, OffsetY = 1 }
            : null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimerViewModel.Phase) && ViewModel.Phase is PomodoroPhase.Work or PomodoroPhase.Break)
            ShowNextQuote();
    }

    private void ShowNextQuote()
    {
        // The user's own words, when they've chosen some, stay put.
        if (_texts.Quote is { Length: > 0 } own)
        {
            QuoteText.Text = own;
            QuoteSource.Text = string.Empty;
            return;
        }
        var quote = Quotes.Random();
        QuoteText.Text = quote.Text;
        QuoteSource.Text = string.IsNullOrEmpty(quote.Source) ? string.Empty : $"— {quote.Source}";
    }

    protected override void OnClosed(System.EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        _texts.PropertyChanged -= OnTextsChanged;
        base.OnClosed(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => MinimizeOrHide();

    private void OnMenuClick(object? sender, RoutedEventArgs e) => OpenActionsMenu(MenuButton);

    protected override Control MenuAnchor => MenuButton;
}
