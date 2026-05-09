using System;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

// Placeholder until the settings shell is built in Step 5.
public class StubShowSettingsCommand : IShowSettingsCommand
{
    public bool CanExecute(object? parameter) => false;
    public void Execute(object? parameter) { }
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
