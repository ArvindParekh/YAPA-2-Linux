using System;
using System.Collections.Generic;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Tests
{
    public class TimerMock : ITimer
    {
        public event Action Tick;
        public bool IsRunning { get; private set; }
        public TimeSpan? LastScheduled { get; private set; }
        public void ScheduleNext(TimeSpan delay) => LastScheduled = delay;

        public void PerformTick() => Tick?.Invoke();
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }

    /// <summary>
    /// Drives both clocks from one settable instant, the way a machine that
    /// never sleeps behaves. <see cref="Sleep"/> advances only wall time.
    /// </summary>
    public class DateMock : IClock
    {
        private static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private TimeSpan _sleptFor;

        public DateTime DateToReturn;

        public DateTime UtcNow => DateToReturn;
        public TimeSpan Monotonic => DateToReturn - Epoch - _sleptFor;

        public void Advance(TimeSpan by) => DateToReturn += by;

        public void Sleep(TimeSpan duration)
        {
            DateToReturn += duration;
            _sleptFor += duration;
        }
    }

    public class InlineThreading : IThreading
    {
        public void RunOnUiThread(Action action) => action();
    }

    public class InMemoryEnvironment : IEnvironment
    {
        public string Settings { get; private set; } = "{}";
        public string LocalSettings { get; private set; } = "{}";

        public string GetSettings() => Settings;
        public void SaveSettings(string settings) => Settings = settings;
        public string GetLocalSettings() => LocalSettings;
        public void SaveLocalSettings(string settings) => LocalSettings = settings;
        public string GetPluginDirectory() => "/nonexistent/plugins";
        public string GetThemeDirectory() => "/nonexistent/themes";
        public bool PreRelease() => false;
    }

    public static class TestSettings
    {
        public static ISettings Create(IEnvironment environment = null)
            => new JsonYapaSettings(environment ?? new InMemoryEnvironment(), new NewtonsoftJson());

        public static PomodoroEngineSettings Engine(ISettings settings, PomodoroProfile profile, string profileName = "test")
        {
            var engineSettings = new PomodoroEngineSettings(settings)
            {
                Profiles = new Dictionary<string, PomodoroProfile> { [profileName] = profile },
            };
            engineSettings.ActiveProfile = profileName;
            return engineSettings;
        }
    }
}
