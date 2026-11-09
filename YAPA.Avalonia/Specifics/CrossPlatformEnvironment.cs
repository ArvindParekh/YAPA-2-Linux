using System.IO;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public class CrossPlatformEnvironment : IEnvironment
{
    private readonly string _settingsFile = Path.Combine(AppPaths.ConfigDirectory, "settings.json");
    private readonly string _localSettingsFile = Path.Combine(AppPaths.ConfigDirectory, "localSettings.json");

    public CrossPlatformEnvironment() => AppPaths.EnsureCreated();

    public string GetSettings() => ReadOrEmpty(_settingsFile);
    public void SaveSettings(string settings) => AtomicFile.WriteAllText(_settingsFile, settings);

    public string GetLocalSettings() => ReadOrEmpty(_localSettingsFile);
    public void SaveLocalSettings(string settings) => AtomicFile.WriteAllText(_localSettingsFile, settings);

    public string GetPluginDirectory() => Path.Combine(AppPaths.DataDirectory, "Plugins");
    public string GetThemeDirectory() => Path.Combine(AppPaths.DataDirectory, "Themes");

    public bool PreRelease() => false;

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "{}";
}
