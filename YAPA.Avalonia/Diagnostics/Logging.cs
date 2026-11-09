using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Diagnostics;

/// <summary>
/// Everything in the app logs through <see cref="Trace"/> with a "[area]" prefix.
/// This routes it to a small rotating file, the first place to look when
/// something misbehaves on a user's machine:
/// <c>~/.cache/YAPA2/yapa2.log</c> (or under <c>YAPA_HOME</c>).
/// </summary>
public static class Logging
{
    private const long MaxBytes = 512 * 1024;

    public static string LogFile { get; } = Path.Combine(AppPaths.CacheDirectory, "yapa2.log");

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxBytes)
                File.Move(LogFile, LogFile + ".1", overwrite: true);

            var writer = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Trace.Listeners.Add(new TimestampedListener(writer));
            Trace.WriteLine($"[app] YAPA 2 {Program.Version} starting (pid {Environment.ProcessId}, {Environment.OSVersion}, isolated={AppPaths.IsIsolated})");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"yapa2: logging disabled: {e.Message}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace.WriteLine($"[crash] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace.WriteLine($"[task] unobserved: {e.Exception}");
            e.SetObserved();
        };
    }

    private sealed class TimestampedListener(TextWriter writer) : TextWriterTraceListener(writer)
    {
        public override void WriteLine(string? message) => base.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }
}
