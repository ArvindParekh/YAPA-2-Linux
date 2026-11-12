using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <param name="Id">What Claude Code's <c>--model</c> takes: an alias, so it follows the latest model of each family.</param>
/// <param name="Short">For the settings list.</param>
/// <param name="Detail">For the model, when it is asked to choose.</param>
public sealed record AssistantModel(string Id, string Name, string Short, string Detail);

public sealed class AssistantSettings : IPluginSettings, INotifyPropertyChanged
{
    public const string DefaultModel = "sonnet";

    public static IReadOnlyList<AssistantModel> Models { get; } =
    [
        new("sonnet", "Sonnet", "recommended", "Quick and capable. Best for most things."),
        new("opus", "Opus", "most thorough", "The most thorough. A little slower to answer."),
        new("haiku", "Haiku", "fastest", "The fastest. Fine for simple questions and commands."),
    ];

    private readonly ISettingsForComponent _s;

    public AssistantSettings(ISettings settings) => _s = settings.GetSettingsForComponent("Assistant");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(object value, [CallerMemberName] string name = "")
    {
        _s.Update(name, value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Double-clicking the timer (and "/") opens the assistant.</summary>
    public bool Enabled
    {
        get => _s.Get(nameof(Enabled), true);
        set => Set(value);
    }

    public string Model
    {
        get
        {
            var stored = _s.Get(nameof(Model), DefaultModel);
            return Models.Any(m => m.Id == stored) ? stored : DefaultModel;
        }
        set => Set(Models.Any(m => m.Id == value) ? value : DefaultModel);
    }

    public void ResetToDefaults()
    {
        Enabled = true;
        Model = DefaultModel;
    }

    public void DeferChanges() => _s.DeferChanges();
}
