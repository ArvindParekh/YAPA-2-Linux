using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace YAPA.Avalonia.Settings.Controls;

/// <summary>
/// A number with − and + on either side, and a unit after it ("50 min"). Replaces the
/// stock NumericUpDown, whose stacked arrows are fiddly targets and read as a form, not a
/// setting. Type a number, click the buttons, or scroll over it.
/// </summary>
public class Stepper : TemplatedControl
{
    public static readonly StyledProperty<decimal?> ValueProperty =
        AvaloniaProperty.Register<Stepper, decimal?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<decimal> MinimumProperty = AvaloniaProperty.Register<Stepper, decimal>(nameof(Minimum), 0);
    public static readonly StyledProperty<decimal> MaximumProperty = AvaloniaProperty.Register<Stepper, decimal>(nameof(Maximum), 100);
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<Stepper, string?>(nameof(Unit));

    public decimal? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public decimal Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public decimal Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private Button? _minus;
    private Button? _plus;
    private TextBox? _text;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_minus != null) _minus.Click -= OnMinus;
        if (_plus != null) _plus.Click -= OnPlus;
        if (_text != null)
        {
            _text.LostFocus -= OnTextCommitted;
            _text.KeyDown -= OnTextKey;
        }

        _minus = e.NameScope.Find<Button>("PART_Minus");
        _plus = e.NameScope.Find<Button>("PART_Plus");
        _text = e.NameScope.Find<TextBox>("PART_Text");
        if (_minus != null) _minus.Click += OnMinus;
        if (_plus != null) _plus.Click += OnPlus;
        if (_text != null)
        {
            _text.LostFocus += OnTextCommitted;
            _text.KeyDown += OnTextKey;
        }
        ShowValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty) ShowValue();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEnabled || e.Delta.Y == 0) return;
        Step(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnMinus(object? sender, RoutedEventArgs e) => Step(-1);

    private void OnPlus(object? sender, RoutedEventArgs e) => Step(+1);

    private void Step(int by) => Value = Math.Clamp((Value ?? Minimum) + by, Minimum, Maximum);

    private void OnTextKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OnTextCommitted(sender, e);
                e.Handled = true;
                break;
            case Key.Up:
                Step(+1);
                e.Handled = true;
                break;
            case Key.Down:
                Step(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnTextCommitted(object? sender, RoutedEventArgs e)
    {
        if (_text == null) return;
        if (decimal.TryParse(_text.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var typed))
            Value = Math.Clamp(Math.Round(typed), Minimum, Maximum);
        ShowValue(); // undo junk, or show the clamped value
    }

    private void ShowValue()
    {
        if (_text != null && !_text.IsFocused) _text.Text = (Value ?? Minimum).ToString("0", CultureInfo.CurrentCulture);
        if (_minus != null) _minus.IsEnabled = (Value ?? Minimum) > Minimum;
        if (_plus != null) _plus.IsEnabled = (Value ?? Minimum) < Maximum;
    }
}
