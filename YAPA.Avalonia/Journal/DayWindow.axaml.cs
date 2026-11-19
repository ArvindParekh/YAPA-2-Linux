using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace YAPA.Avalonia.Journal;

/// <summary>The day journal's window. The view model does the work; this is focus, copying and scrolling.</summary>
public partial class DayWindow : Window
{
    private readonly DayViewModel _model;
    private readonly DispatcherTimer _copied;

    public DayWindow() : this(new DayViewModel(App.Bootstrapper!.Resolve<JournalService>(), App.Bootstrapper!.Resolve<JournalSettings>())) { }

    public DayWindow(DayViewModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        // Before the first map, so the frame comes up dark rather than flipping.
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle)
            YAPA.Avalonia.Platform.Linux.X11.X11Window.PreferDarkFrame(handle.Handle);
        Ribbon.BlockClicked += id => _model.RevealPomodoro(id);
        _model.Reveal += ScrollTo;
        _copied = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _copied.Tick += (_, _) =>
        {
            _copied.Stop();
            CopyLabel.Text = "Copy";
        };
        Closed += (_, _) => _model.Dispose();
    }

    public void Open(DateOnly? date = null) => _model.Open(date);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.Left: _model.PreviousCommand.Execute(null); e.Handled = true; break;
            case Key.Right: _model.NextCommand.Execute(null); e.Handled = true; break;
            case Key.T: _model.TodayCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = _model.AsText();
        if (text.Length == 0 || Clipboard is not { } clipboard) return;
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
