using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using YAPA.Avalonia.SingleInstance;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>Executes command-line commands, from the first launch or forwarded by later ones.</summary>
public sealed class AppCommandHandler(TimerViewModel vm, IAppNavigator navigator)
{
    // Read by scripts and people: "+04:12" stays readable rather than "\u002B04:12".
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <returns>Text to print in the terminal that sent the commands.</returns>
    public string Execute(IReadOnlyList<AppCommand> commands)
    {
        var output = new List<string>();
        foreach (var command in commands)
        {
            switch (command)
            {
                case AppCommand.Show: navigator.ShowTimer(); break;
                case AppCommand.Hide: navigator.HideTimer(); break;
                case AppCommand.ToggleVisibility: navigator.ToggleTimerVisibility(); break;
                case AppCommand.Start: vm.Engine.Start(); break;
                case AppCommand.Pause: vm.Engine.Pause(); break;
                case AppCommand.Toggle: vm.ToggleCommand.Execute(null); break;
                case AppCommand.Stop: vm.Engine.Stop(); break;
                case AppCommand.Skip: vm.Engine.Skip(); break;
                case AppCommand.LongBreak: vm.Engine.StartLongBreak(); break;
                case AppCommand.ToggleAmbience: vm.ToggleAmbienceCommand.Execute(null); break;
                case AppCommand.Reset: vm.Engine.Reset(); break;
                case AppCommand.MarkDone: vm.Engine.MarkDone(); break;
                case AppCommand.Settings: navigator.ShowSettings(); break;
                case AppCommand.Statistics: navigator.ShowStatistics(); break;
                case AppCommand.Status: output.Add(StatusLine()); break;
                case AppCommand.StatusJson: output.Add(StatusJson()); break;
                case AppCommand.Quit: navigator.Quit(); break;
            }
        }

        // A state-changing command answers with the new state, which is handy from a shortcut script.
        if (output.Count == 0 && commands.Any(c => c is not (AppCommand.Show or AppCommand.Hide or AppCommand.Quit)))
            output.Add(StatusLine());

        return string.Join('\n', output);
    }

    private string StatusLine()
    {
        vm.Refresh();
        return vm.Phase == PomodoroPhase.NotStarted
            ? $"Ready ({vm.CounterTooltip.ToLowerInvariant()})"
            : $"{vm.StatusText} {vm.TimeText}";
    }

    private string StatusJson()
    {
        vm.Refresh();
        var e = vm.Engine;
        return JsonSerializer.Serialize(new
        {
            running = true,
            phase = e.Phase.ToString(),
            pausedPhase = e.Phase == PomodoroPhase.Pause ? e.PausedPhase.ToString() : null,
            status = vm.StatusText,
            display = vm.TimeText,
            elapsed = e.Elapsed,
            remaining = e.Remaining,
            length = e.CurrentIntervalLength,
            pomodoro = e.Index,
            cycle = e.Cycle,
            longBreakNext = e.IsLongBreak,
            counter = e.Counter,
            overrun = e.Overrun,
            goal = vm.Goal.HasGoal
                ? new
                {
                    target = vm.Goal.Goal,
                    done = vm.Goal.Done,
                    finish = vm.Goal.Finish?.ToString("yyyy-MM-ddTHH:mm"),
                    finishWithoutLongBreaks = (vm.Goal.FinishWithoutLongBreaks ?? vm.Goal.Finish)?.ToString("yyyy-MM-ddTHH:mm"),
                }
                : null,
        }, JsonOptions);
    }
}
