using System;
using System.Diagnostics;

namespace YAPA.Avalonia.Audio;

/// <summary>A running external player. Abstracted so the supervision logic can be tested without spawning anything.</summary>
internal interface IPlayerProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Null while running, or when the exit code couldn't be read.</summary>
    int? ExitCode { get; }

    /// <summary>Last non-empty stderr line, for diagnosing a player that refuses a file.</summary>
    string? LastErrorLine { get; }

    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

internal interface IProcessLauncher
{
    /// <summary>
    /// Starts the command. <paramref name="onExited"/> is wired up before the process starts,
    /// so an instant exit can't be missed. May throw if the executable can't be started.
    /// </summary>
    IPlayerProcess Start(PlayerCommand command, Action<IPlayerProcess> onExited);
}

internal sealed class SystemProcessLauncher : IProcessLauncher
{
    public static SystemProcessLauncher Instance { get; } = new();

    public IPlayerProcess Start(PlayerCommand command, Action<IPlayerProcess> onExited)
        => SystemPlayerProcess.Start(command, onExited);

    private sealed class SystemPlayerProcess : IPlayerProcess
    {
        private readonly Process _process;
        private volatile string? _lastErrorLine;

        private SystemPlayerProcess(Process process) => _process = process;

        public static SystemPlayerProcess Start(PlayerCommand command, Action<IPlayerProcess> onExited)
        {
            var info = new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // All three are redirected so a player can't read keystrokes from, or scribble
                // progress output over, the terminal YAPA was started from.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in command.Arguments)
                info.ArgumentList.Add(arg);

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var wrapper = new SystemPlayerProcess(process);
            process.Exited += (_, _) => onExited(wrapper);
            // Output must be drained or a chatty player blocks once the pipe buffer fills.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    wrapper._lastErrorLine = e.Data;
            };

            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return wrapper;
        }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try { return _process.HasExited ? _process.ExitCode : null; }
                catch { return null; }
            }
        }

        public string? LastErrorLine => _lastErrorLine;

        public void Kill()
        {
            // The whole tree: gst-play and some ffplay builds hand decoding to helper processes.
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, or exiting as we speak; either way it is not playing.
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            try { return _process.WaitForExit(timeout); }
            catch { return true; }
        }

        public void Dispose() => _process.Dispose();
    }
}
