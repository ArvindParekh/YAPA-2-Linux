using System;
using System.Diagnostics;
using System.IO;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Cross-platform audio player that delegates to system utilities.
/// Linux: paplay (PulseAudio) → aplay (ALSA) for WAV; ffplay → mpg123 for other formats.
/// macOS: afplay.
/// Windows: PowerShell New-Object Media.SoundPlayer (WAV only fallback).
/// </summary>
public sealed class ProcessBasedMusicPlayer : IMusicPlayer, IDisposable
{
    private Process? _process;
    private string? _loadedPath;
    private bool _repeat;
    private double _volume = 0.5;

    public bool IsPlaying => _process is { HasExited: false };

    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        _loadedPath = path;
    }

    public void Play(bool repeat = false, double volume = 0.5)
    {
        if (IsPlaying || !File.Exists(_loadedPath)) return;
        _repeat = repeat;
        _volume = volume;
        StartProcess();
    }

    private void StartProcess()
    {
        var info = BuildInfo(_loadedPath!, _volume);
        if (info == null) return;

        _process = Process.Start(info);
        if (_process == null) return;

        if (_repeat)
        {
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_repeat && File.Exists(_loadedPath))
            StartProcess();
    }

    public void Stop()
    {
        _repeat = false;
        if (_process != null)
        {
            _process.Exited -= OnProcessExited;
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process = null;
        }
    }

    public void Dispose() => Stop();

    // ── Command selection ─────────────────────────────────────────────────────

    // Builds a ProcessStartInfo using ArgumentList so paths with spaces are passed
    // correctly. When UseShellExecute=false, the old single-string args form passes
    // literal quote characters to the child process, causing "file not found" errors.
    private static ProcessStartInfo? BuildInfo(string path, double volume)
    {
        if (OperatingSystem.IsLinux())
        {
            var isWav = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            if (isWav)
            {
                // paplay accepts --volume 0-65536; scale linearly from 0-1
                return Try("paplay", $"--volume={(int)(volume * 65536)}", path)
                    ?? Try("aplay", path);
            }
            else
            {
                return Try("ffplay", "-nodisp", "-autoexit", $"-volume {(int)(volume * 100)}", path)
                    ?? Try("mpg123", "-q", path);
            }
        }

        if (OperatingSystem.IsMacOS())
            return Try("afplay", $"-v {volume:F2}", path);

        if (OperatingSystem.IsWindows())
            return Try("powershell", "-NonInteractive", "-Command",
                $"(New-Object Media.SoundPlayer '{path}').PlaySync()");

        return null;
    }

    private static ProcessStartInfo? Try(string command, params string[] args)
    {
        try
        {
            using var check = Process.Start(new ProcessStartInfo("which", command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            check?.WaitForExit(500);
            if (check?.ExitCode != 0) return null;
        }
        catch
        {
            // 'which' not available on Windows — fall through and try anyway
        }

        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }
}
