using System;
using System.IO;
using System.Linq;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Persistence;

/// <summary>
/// Saves the pomodoro engine state on shutdown and restores it on next launch,
/// matching the snapshot behaviour of the WPF App.xaml.cs.
///
/// Step 3: auto-resume when /start is passed; interactive "Resume?" dialog is
/// wired up in Step 4 once the main window is available.
/// </summary>
public sealed class SnapshotService
{
    private readonly IPomodoroEngine _engine;
    private readonly IDate _date;
    private readonly IJson _json;
    private readonly string _snapshotPath;

    public SnapshotService(IPomodoroEngine engine, IDate date, IJson json, IEnvironment environment)
    {
        _engine = engine;
        _date = date;
        _json = json;

        // Derive the data directory from the plugin directory path.
        var dataDir = Path.GetDirectoryName(environment.GetPluginDirectory())!;
        Directory.CreateDirectory(dataDir);
        _snapshotPath = Path.Combine(dataDir, "snapshot.json");
    }

    /// <summary>
    /// Writes the engine snapshot to disk. Called just before the app exits.
    /// </summary>
    public void SaveSnapshot()
    {
        try
        {
            var snapshot = _engine.GetSnapshot();
            File.WriteAllText(_snapshotPath, _json.Serialize(snapshot));
        }
        catch { /* ignore I/O errors on shutdown */ }
    }

    /// <summary>
    /// Loads the snapshot from disk and optionally resumes the engine.
    /// <paramref name="startArgs"/> is the startup command-line argument array;
    /// auto-resume happens when <see cref="CommandLineConstants.Start"/> is present.
    /// Returns true if a snapshot was found and the caller should offer an interactive resume dialog.
    /// </summary>
    public (bool ShouldAskResume, PomodoroEngineSnapshot? Snapshot) TryLoadSnapshot(string[] startArgs)
    {
        try
        {
            if (!File.Exists(_snapshotPath))
                return (false, null);

            var json = File.ReadAllText(_snapshotPath);
            if (string.IsNullOrEmpty(json))
                return (false, null);

            var snapshot = _json.Deserialize<PomodoroEngineSnapshot>(json);
            if (snapshot == null)
                return (false, null);

            if (snapshot.Phase != PomodoroPhase.Work && snapshot.Phase != PomodoroPhase.Pause)
                return (false, null);

            var startImmediately = startArgs
                .Select(a => a.ToLowerInvariant())
                .Contains(CommandLineConstants.Start);

            if (startImmediately)
            {
                ApplySnapshot(snapshot);
                return (false, null);
            }

            // Caller (the main window, Step 4) will show the "Resume?" dialog.
            return (true, snapshot);
        }
        catch
        {
            return (false, null);
        }
        finally
        {
            DeleteSnapshotFile();
        }
    }

    /// <summary>
    /// Applies an already-loaded snapshot to the engine (used by the dialog path in Step 4).
    /// </summary>
    public void ApplySnapshot(PomodoroEngineSnapshot snapshot)
    {
        snapshot.StartDate = _date.DateTimeUtc();
        _engine.LoadSnapshot(snapshot);
    }

    private void DeleteSnapshotFile()
    {
        try
        {
            if (File.Exists(_snapshotPath))
                File.Delete(_snapshotPath);
        }
        catch { /* ignore */ }
    }
}
