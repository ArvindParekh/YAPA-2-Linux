using System;
using Avalonia.Controls;

namespace YAPA.Avalonia.Windows;

public partial class TimerContextMenu : ContextMenu
{
    public TimerContextMenu() => InitializeComponent();

    // Avalonia finds control themes by exact type. Without this the subclass gets
    // no ContextMenu template, so it "opens" as an empty, unmeasured popup that
    // never appears on screen.
    protected override Type StyleKeyOverride => typeof(ContextMenu);
}
