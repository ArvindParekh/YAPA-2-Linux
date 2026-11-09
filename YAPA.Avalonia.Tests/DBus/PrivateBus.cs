using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YAPA.Avalonia.Tests.DBus;

/// <summary>
/// A throwaway dbus-daemon on a private socket, so tests never touch the user's session bus
/// (where a Notify would pop up on their screen).
///
/// The config has no servicedir, so nothing gets D-Bus-activated: a call to a name nobody owns
/// fails with ServiceUnknown, as it would before gnome-shell claims the name.
/// </summary>
internal sealed class PrivateBus : IDisposable
{
    private readonly string _daemon;
    private readonly string _directory;
    private readonly string _socket;
    private readonly string _config;
    private Process? _process;

    /// <summary>Plain socket address without the daemon's guid, so it stays valid across <see cref="Restart"/>.</summary>
    public string Address => "unix:path=" + _socket;

    private PrivateBus(string daemon)
    {
        _daemon = daemon;
        // Short path: unix socket paths are limited to 108 bytes.
        _directory = Path.Combine("/tmp", "yapa-dbus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _socket = Path.Combine(_directory, "bus");
        _config = Path.Combine(_directory, "bus.conf");
        File.WriteAllText(_config, $"""
            <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
              "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
            <busconfig>
              <type>session</type>
              <listen>unix:path={_socket}</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);
    }

    public static PrivateBus StartOrInconclusive()
    {
        var daemon = FindDaemon();
        if (daemon is null)
            Assert.Inconclusive("dbus-daemon not found; skipping D-Bus integration test");
        var bus = new PrivateBus(daemon);
        try
        {
            bus.Start();
        }
        catch (Exception ex)
        {
            bus.Dispose();
            Assert.Inconclusive($"could not start a private dbus-daemon: {ex.Message}");
        }
        return bus;
    }

    /// <summary>Kills the daemon, which drops every client connection.</summary>
    public void Stop()
    {
        if (_process is null)
            return;
        try
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
        _process = null;
    }

    /// <summary>Simulates the bus going away and coming back at the same address.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    private void Start()
    {
        // A killed daemon leaves its socket file behind, and binding over it fails.
        File.Delete(_socket);
        var startInfo = new ProcessStartInfo(_daemon)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--config-file=" + _config);
        startInfo.ArgumentList.Add("--nofork");
        startInfo.ArgumentList.Add("--print-address");
        var errors = new System.Text.StringBuilder();
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
        // Drain stderr so a chatty daemon can never block on a full pipe.
        _process.ErrorDataReceived += (_, e) => { lock (errors) errors.AppendLine(e.Data); };
        _process.BeginErrorReadLine();

        // The address is printed once the socket is listening, so it doubles as a readiness signal.
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(10)) || string.IsNullOrEmpty(read.Result))
        {
            Stop();
            lock (errors)
                throw new InvalidOperationException("dbus-daemon did not report an address: " + errors);
        }
    }

    private static string? FindDaemon()
    {
        var candidates = new[] { "/usr/bin/dbus-daemon" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, "dbus-daemon")));
        return OperatingSystem.IsLinux() ? candidates.FirstOrDefault(File.Exists) : null;
    }

    public void Dispose()
    {
        Stop();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
