using System;
using System.Diagnostics;
using System.Globalization;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Times of day written the way the desktop's own clock writes them. GNOME keeps its
/// 12/24-hour choice in a setting of its own, independent of the locale, and an en_US
/// locale with a 24-hour top bar is common: "6:40 PM" next to a clock reading 18:40
/// would look foreign. Elsewhere, and if GNOME can't be asked, the locale decides.
/// </summary>
public static class ClockFormat
{
    private static readonly Lazy<bool> Uses24HourClock = new(Detect);

    public static string ShortTime(DateTime local)
        => local.ToString(Uses24HourClock.Value ? "HH:mm" : CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            CultureInfo.CurrentCulture);

    /// <summary>An hour mark for a chart axis: "9" on a 24-hour clock, "9 AM" on a 12-hour one.</summary>
    public static string Hour(int hour)
        => Uses24HourClock.Value ? hour.ToString(CultureInfo.CurrentCulture)
            : DateTime.Today.AddHours(hour).ToString("h tt", CultureInfo.CurrentCulture);

    private static bool Detect()
    {
        var fromLocale = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        if (!OperatingSystem.IsLinux()) return fromLocale;
        try
        {
            var info = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface clock-format")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info);
            if (process == null) return fromLocale;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1000))
            {
                process.Kill();
                process.WaitForExit(); // reap it, or it lingers as a zombie
                return fromLocale;
            }
            return output.Result.Trim() switch
            {
                "'24h'" => true,
                "'12h'" => false,
                _ => fromLocale, // no GNOME schema: some other desktop
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return fromLocale; // no gsettings at all
        }
    }
}
