using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Your day and your week, as a page of the Hub. The view model (the Hub's, kept for the window's
/// life) does the work; this is focus, copying, flyouts and scrolling.
/// </summary>
public partial class JournalView : UserControl
{
    private DayViewModel? _model;
    private readonly DispatcherTimer _copied;

    public JournalView()
    {
        InitializeComponent();
        Ribbon.BlockClicked += id => _model?.RevealPomodoro(id);
        _copied = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _copied.Tick += (_, _) =>
        {
            _copied.Stop();
            CopyLabel.Text = "Copy";
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null)
        {
            _model.Reveal -= ScrollTo;
            _model.CalendarDone -= HideCalendar;
        }
        _model = DataContext as DayViewModel;
        if (_model != null)
        {
            _model.Reveal += ScrollTo;
            _model.CalendarDone += HideCalendar;
        }
    }

    private void HideCalendar() => DateButton.Flyout?.Hide();

    private DayViewModel Model => _model!;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _model == null || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.Left: Model.PreviousCommand.Execute(null); e.Handled = true; break;
            case Key.Right: Model.NextCommand.Execute(null); e.Handled = true; break;
            case Key.T: Model.TodayCommand.Execute(null); e.Handled = true; break;
            case Key.C: DateButton.Flyout?.ShowAt(DateButton); e.Handled = true; break;
            case Key.W:
                if (Model.IsWeek) Model.ShowDayViewCommand.Execute(null);
                else Model.ShowWeekCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnCalendarOpening(object? sender, EventArgs e) => Model.PrepareCalendar();

    private void OnWeekDayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WeekDayRow row } && !row.IsEmpty) Model.OpenDay(row.Date);
    }

    private void OnForgetOpening(object? sender, EventArgs e) => Model.PrepareForgetRange();

    private void OnEntryForgetOpening(object? sender, EventArgs e)
    {
        if (sender is FlyoutBase { Target.DataContext: EntryViewModel entry }) entry.PrepareForget();
    }

    private void OnForgetRangeClick(object? sender, RoutedEventArgs e)
    {
        if (Model.ForgetTypedRange()) CloseFlyout(sender);
    }

    /// <summary>A choice in a flyout was made (or Cancel): the flyout goes.</summary>
    private void OnFlyoutDone(object? sender, RoutedEventArgs e) => CloseFlyout(sender);

    private static void CloseFlyout(object? sender)
    {
        // A flyout's content sits in a Popup, which is its logical parent. Closed after the click
        // is done with: a button runs its Command after its Click handlers, and closing first takes
        // it out of the tree, its inherited DataContext and so its Command with it.
        if (sender is Control control && control.FindLogicalAncestorOfType<Popup>() is { } popup)
            Dispatcher.UIThread.Post(() => popup.Close(), DispatcherPriority.Input);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = Model.AsText();
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        CopyLabel.Text = "Copied";
        _copied.Stop();
        _copied.Start();
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: EntryViewModel entry } title) return;
        entry.EditCommand.Execute(null);
        FocusEditor(title);
    }

    /// <summary>The rename box sits next to the title; it gets the caret once it's shown.</summary>
    private static void FocusEditor(Control title)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (title.Parent is Panel panel && panel.Children.OfType<TextBox>().FirstOrDefault() is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: EntryViewModel entry }) return;
        if (e.Key == Key.Enter)
        {
            entry.SaveEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            entry.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: EntryViewModel entry }) entry.SaveEditCommand.Execute(null);
    }

    private void ScrollTo(EntryViewModel entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = Scroller.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("entry") && ReferenceEquals(b.DataContext, entry));
            if (target == null) return;
            var top = target.TranslatePoint(new Point(0, 0), (Visual)Scroller.Content!);
            if (top is { } p) Scroller.Offset = new Vector(0, Math.Max(0, p.Y - 80));
        }, DispatcherPriority.Background);
    }
}
