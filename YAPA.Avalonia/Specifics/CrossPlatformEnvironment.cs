using System;
using System.IO;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public class CrossPlatformEnvironment : IEnvironment
{
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly string _settingsFile;
    private readonly string _localSettingsFile;

    public CrossPlatformEnvironment()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Respect XDG Base Directory specification
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            _configDir = Path.Combine(xdgConfig, "YAPA2");
            _dataDir = Path.Combine(xdgData, "YAPA2");
        }
        else
        {
            // Windows: match WPF path so existing settings files are reused
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _configDir = _dataDir = Path.Combine(docs, "YAPA2");
        }

        _settingsFile = Path.Combine(_configDir, "settings.json");
        _localSettingsFile = Path.Combine(_configDir, "localSettings.json");

        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_dataDir);
    }

    public string GetSettings()
    {
        if (!File.Exists(_settingsFile))
            return "{}";
        return File.ReadAllText(_settingsFile);
    }

    public void SaveSettings(string settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        File.WriteAllText(_settingsFile, settings);
    }

    public string GetLocalSettings()
    {
        if (!File.Exists(_localSettingsFile))
            return "{}";
        return File.ReadAllText(_localSettingsFile);
    }

    public void SaveLocalSettings(string settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_localSettingsFile)!);
        File.WriteAllText(_localSettingsFile, settings);
    }

    public string GetPluginDirectory()
        => Path.Combine(_dataDir, "Plugins");

    public string GetThemeDirectory()
        => Path.Combine(_dataDir, "Themes");

    public bool PreRelease()
        => File.Exists(Path.Combine(_configDir, "PreRelease.txt"));

    public string DataDirectory => _dataDir;
    public string ConfigDirectory => _configDir;
}
