using System;
using System.Windows.Input;
using YAPA.Avalonia.Settings;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class ShowSettingsCommand : IShowSettingsCommand
{
    private SettingsWindow? _window;

#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }
        _window = new SettingsWindow();
        _window.Show();
    }
}
