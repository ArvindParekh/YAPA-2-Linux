using System;
using System.ComponentModel;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Common
{
    public class PomodoroEngineSnapshot
    {
        public PomodoroProfile PomodoroProfile { get; set; }
        public string ProfileName { get; set; }

        /// <summary>Seconds elapsed in the interval when the snapshot was taken.</summary>
        public int PausedTime { get; set; }

        /// <summary>UTC time the snapshot was taken.</summary>
        public DateTime StartDate { get; set; }
        public PomodoroPhase Phase { get; set; }

        /// <summary>Only meaningful when <see cref="Phase"/> is Pause. Absent in snapshots from older builds (treated as Work).</summary>
        public PomodoroPhase? PausedPhase { get; set; }
        public int PomodoroIndex { get; set; }

        /// <summary>The user asked for the long break early (see <see cref="PomodoroEngine.StartLongBreak"/>).</summary>
        public bool LongBreakRequested { get; set; }
    }

    /// <summary>
    /// The pomodoro state machine.
    ///
    ///   NotStarted ─start→ Work ─(time up)→ WorkEnded ─start→ Break ─(time up)→ BreakEnded ─start→ Work …
    ///                       │  ↑                                 │  ↑
    ///                     pause│start                          pause│start
    ///                       ↓  │                                 ↓  │
    ///                       Pause (PausedPhase = Work)           Pause (PausedPhase = Break)
    ///
    /// Elapsed time is accumulated in segments on a monotonic clock: each pause
    /// folds the running segment into <see cref="_accumulated"/>. All methods are
    /// expected to be called on the UI thread; the timer ticks there too.
    /// </summary>
    public class PomodoroEngine : IPomodoroEngine, IPlugin
    {
        /// <summary>
        /// Pause between "work ended" and an auto-started break, so the completion
        /// sound and flash register before the break takes over.
        /// </summary>
        public static readonly TimeSpan AutoStartDelay = TimeSpan.FromSeconds(1.5);

        private readonly PomodoroEngineSettings _settings;
        private readonly ITimer _timer;
        private readonly IClock _clock;
        private readonly IThreading _threading;
        private readonly IPomodoroRepository _repository;

        private PomodoroPhase _phase = PomodoroPhase.NotStarted;
        private PomodoroPhase _pausedPhase = PomodoroPhase.Work;
        private int _index = 1;
        private bool _longBreakRequested;
        private int _completedThisSession;

        private TimeSpan _accumulated;
        private TimeSpan _segmentStart;
        private TimeSpan? _autoStartAt;
        private int _lastNotifiedSecond = -1;

        public PomodoroEngine(PomodoroEngineSettings settings, ITimer timer, IClock clock, IThreading threading, ISettings globalSettings, IPomodoroRepository repository)
        {
            _settings = settings;
            _timer = timer;
            _clock = clock;
            _threading = threading;
            _repository = repository;

            _timer.Tick += OnTick;
            globalSettings.PropertyChanged += OnGlobalSettingsChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<PomodoroTransition> Transitioned;
        public event Action OnPomodoroCompleted;

        // ── State ────────────────────────────────────────────────────────────

        public PomodoroPhase Phase => _phase;

        public PomodoroPhase PausedPhase => _pausedPhase;

        public int Index => Math.Min(_index, CycleLength);

        public bool IsRunning => _phase == PomodoroPhase.Work || _phase == PomodoroPhase.Break;

        public bool IsLongBreak => _longBreakRequested || _index >= CycleLength;

        public int WorkTime => Math.Max(1, _settings.WorkTime);

        public int BreakTime => Math.Max(1, IsLongBreak ? _settings.LongBreakTime : _settings.BreakTime);

        private int CycleLength => Math.Max(1, _settings.PomodorosBeforeLongBreak);

        /// <summary>Whether the interval being timed (or about to be) is a break.</summary>
        private bool InBreakInterval
        {
            get
            {
                switch (_phase)
                {
                    case PomodoroPhase.Break:
                    case PomodoroPhase.BreakEnded:
                        return true;
                    case PomodoroPhase.Pause:
                        return _pausedPhase == PomodoroPhase.Break;
                    default:
                        return false;
                }
            }
        }

        public int CurrentIntervalLength => InBreakInterval ? BreakTime : WorkTime;

        public int Elapsed
        {
            get
            {
                var elapsed = _accumulated;
                if (IsRunning)
                {
                    elapsed += _clock.Monotonic - _segmentStart;
                }
                else if (_phase == PomodoroPhase.WorkEnded || _phase == PomodoroPhase.BreakEnded)
                {
                    return CurrentIntervalLength;
                }

                var seconds = (int)Math.Floor(elapsed.TotalSeconds);
                return Math.Max(0, Math.Min(seconds, CurrentIntervalLength));
            }
        }

        public int Remaining => CurrentIntervalLength - Elapsed;

        public int DisplayValue => _settings.CountBackwards ? Remaining : Elapsed;

        public double Progress => CurrentIntervalLength <= 0 ? 0 : (double)Elapsed / CurrentIntervalLength;

        public int Counter
        {
            get
            {
                switch (_settings.Counter)
                {
                    case CounterEnum.CompletedToday:
                        return _repository.CompletedToday();
                    case CounterEnum.CompletedThisSession:
                        return _completedThisSession;
                    default:
                        return Index;
                }
            }
        }

        // ── Commands ─────────────────────────────────────────────────────────

        public void Start() => Start(automatic: false);

        private void Start(bool automatic)
        {
            var from = _phase;
            _autoStartAt = null;

            switch (_phase)
            {
                case PomodoroPhase.NotStarted:
                    BeginInterval(PomodoroPhase.Work, TimeSpan.Zero);
                    Raise(PomodoroTransitionKind.WorkStarted, from, automatic);
                    break;

                case PomodoroPhase.BreakEnded:
                    AdvanceIndex();
                    BeginInterval(PomodoroPhase.Work, TimeSpan.Zero);
                    Raise(PomodoroTransitionKind.WorkStarted, from, automatic);
                    break;

                case PomodoroPhase.WorkEnded:
                    BeginInterval(PomodoroPhase.Break, TimeSpan.Zero);
                    Raise(PomodoroTransitionKind.BreakStarted, from, automatic);
                    break;

                case PomodoroPhase.Pause:
                    _segmentStart = _clock.Monotonic;
                    SetPhase(_pausedPhase);
                    _timer.Start();
                    NotifyTime(force: true);
                    Raise(PomodoroTransitionKind.Resumed, from, automatic);
                    break;

                default:
                    // Already running: starting again is meaningless, not an error.
                    // Commands arrive from the tray, CLI and notifications, which can race the UI.
                    return;
            }
        }

        public void Pause()
        {
            if (!IsRunning) return;

            var from = _phase;
            _timer.Stop();
            _accumulated += _clock.Monotonic - _segmentStart;
            _pausedPhase = _phase;
            SetPhase(PomodoroPhase.Pause);
            NotifyTime(force: true);
            Raise(PomodoroTransitionKind.Paused, from, false);
        }

        public void Stop()
        {
            var from = _phase;
            var breakIsOver = false;

            switch (_phase)
            {
                case PomodoroPhase.NotStarted:
                    return;
                case PomodoroPhase.Work:
                    // Abandoned pomodoro: retry the same one, record nothing.
                    break;
                case PomodoroPhase.Pause:
                    breakIsOver = _pausedPhase == PomodoroPhase.Break;
                    break;
                case PomodoroPhase.WorkEnded:
                case PomodoroPhase.Break:
                case PomodoroPhase.BreakEnded:
                    // The pomodoro itself was completed; stopping ends (or skips) its break.
                    breakIsOver = true;
                    break;
            }

            var wasLongBreak = IsLongBreak;
            if (breakIsOver) AdvanceIndex();
            ResetInterval();
            Raise(PomodoroTransitionKind.Stopped, from, false, wasLongBreak);
        }

        public void Skip()
        {
            var skippingBreak = _phase == PomodoroPhase.WorkEnded
                || _phase == PomodoroPhase.Break
                || _phase == PomodoroPhase.BreakEnded
                || (_phase == PomodoroPhase.Pause && _pausedPhase == PomodoroPhase.Break);
            if (!skippingBreak) return;

            var from = _phase;
            AdvanceIndex();
            ResetInterval();
            BeginInterval(PomodoroPhase.Work, TimeSpan.Zero);
            Raise(PomodoroTransitionKind.WorkStarted, from, false);
        }

        public bool CanStartLongBreak
        {
            get
            {
                switch (_phase)
                {
                    case PomodoroPhase.NotStarted:
                    case PomodoroPhase.WorkEnded:
                    case PomodoroPhase.BreakEnded:
                        return true;
                    case PomodoroPhase.Break:
                        return !IsLongBreak;
                    case PomodoroPhase.Pause:
                        return _pausedPhase == PomodoroPhase.Break;
                    default:
                        return false;
                }
            }
        }

        public void StartLongBreak()
        {
            if (!CanStartLongBreak) return;

            var from = _phase;
            _autoStartAt = null;
            _longBreakRequested = true;

            if (_phase == PomodoroPhase.Break || _phase == PomodoroPhase.Pause)
            {
                // Stretch the break that's already under way; time already rested counts.
                if (_phase == PomodoroPhase.Pause)
                {
                    _segmentStart = _clock.Monotonic;
                    SetPhase(PomodoroPhase.Break);
                    _timer.Start();
                }
                NotifyPropertyChanged(nameof(IsLongBreak));
                NotifyPropertyChanged(nameof(BreakTime));
                NotifyPropertyChanged(nameof(CurrentIntervalLength));
                NotifyPropertyChanged(nameof(CanStartLongBreak));
                NotifyTime(force: true);
            }
            else
            {
                // From idle or just after an interval: no index advance here, because the
                // long break ends the cycle and the next advance resets it to 1.
                BeginInterval(PomodoroPhase.Break, TimeSpan.Zero);
                NotifyPropertyChanged(nameof(IsLongBreak));
                NotifyPropertyChanged(nameof(BreakTime));
            }

            Raise(PomodoroTransitionKind.BreakStarted, from, false, isLongBreak: true);
        }

        public void Reset()
        {
            var from = _phase;
            if (from == PomodoroPhase.NotStarted && _index == 1) return;

            _longBreakRequested = false;
            SetIndex(1);
            ResetInterval();
            Raise(PomodoroTransitionKind.Reset, from, false);
        }

        // ── Snapshot ─────────────────────────────────────────────────────────

        public PomodoroEngineSnapshot GetSnapshot()
        {
            _settings.Profiles.TryGetValue(_settings.ActiveProfile, out var profile);
            return new PomodoroEngineSnapshot
            {
                PomodoroProfile = profile,
                ProfileName = _settings.ActiveProfile,
                PausedTime = Elapsed,
                Phase = _phase,
                PausedPhase = _phase == PomodoroPhase.Pause ? _pausedPhase : (PomodoroPhase?)null,
                StartDate = _clock.UtcNow,
                PomodoroIndex = Index,
                LongBreakRequested = _longBreakRequested,
            };
        }

        /// <summary>
        /// Restores a snapshot taken by a previous run. A running interval resumes
        /// running; the time the app was closed is not counted.
        /// </summary>
        public void LoadSnapshot(PomodoroEngineSnapshot snapshot)
        {
            if (snapshot == null) return;

            var from = _phase;
            _timer.Stop();
            _autoStartAt = null;

            if (!string.IsNullOrEmpty(snapshot.ProfileName) && _settings.Profiles.ContainsKey(snapshot.ProfileName))
            {
                _settings.ActiveProfile = snapshot.ProfileName;
            }

            SetIndex(Math.Max(1, snapshot.PomodoroIndex));
            _longBreakRequested = snapshot.LongBreakRequested;
            _pausedPhase = snapshot.PausedPhase == PomodoroPhase.Break ? PomodoroPhase.Break : PomodoroPhase.Work;
            _accumulated = TimeSpan.FromSeconds(Math.Max(0, snapshot.PausedTime));
            _segmentStart = _clock.Monotonic;

            var phase = snapshot.Phase;
            SetPhase(phase);
            if (IsRunning) _timer.Start();

            NotifyAll();
            Raise(PomodoroTransitionKind.Restored, from, false);
        }

        // ── Internals ────────────────────────────────────────────────────────

        private void OnTick()
        {
            if (_phase == PomodoroPhase.WorkEnded && _autoStartAt.HasValue)
            {
                if (_clock.Monotonic >= _autoStartAt.Value) Start(automatic: true);
                return;
            }

            if (!IsRunning)
            {
                _timer.Stop();
                return;
            }

            var length = CurrentIntervalLength;
            var elapsed = _accumulated + (_clock.Monotonic - _segmentStart);
            if (elapsed.TotalSeconds >= length)
            {
                CompleteInterval();
                return;
            }

            NotifyTime(force: false);
        }

        private void CompleteInterval()
        {
            var from = _phase;
            _accumulated = TimeSpan.Zero;

            if (from == PomodoroPhase.Work)
            {
                SetPhase(PomodoroPhase.WorkEnded);
                _completedThisSession++;
                NotifyTime(force: true);

                if (_settings.AutoStartBreak)
                {
                    // Keep ticking so the delayed auto-start can fire.
                    _autoStartAt = _clock.Monotonic + AutoStartDelay;
                }
                else
                {
                    _timer.Stop();
                }

                OnPomodoroCompleted?.Invoke();
                NotifyPropertyChanged(nameof(Counter));
                Raise(PomodoroTransitionKind.WorkCompleted, from, true);
            }
            else
            {
                _timer.Stop();
                SetPhase(PomodoroPhase.BreakEnded);
                NotifyTime(force: true);
                var wasLongBreak = IsLongBreak;
                Raise(PomodoroTransitionKind.BreakCompleted, from, true, wasLongBreak);

                if (_settings.AutoStartWork)
                {
                    // No delay here: the break-completed cue is what tells the
                    // user that work has begun.
                    Start(automatic: true);
                }
            }
        }

        private void BeginInterval(PomodoroPhase phase, TimeSpan alreadyElapsed)
        {
            _accumulated = alreadyElapsed;
            _segmentStart = _clock.Monotonic;
            SetPhase(phase);
            _timer.Start();
            NotifyTime(force: true);
        }

        private void ResetInterval()
        {
            _timer.Stop();
            _autoStartAt = null;
            _accumulated = TimeSpan.Zero;
            _pausedPhase = PomodoroPhase.Work;
            SetPhase(PomodoroPhase.NotStarted);
            NotifyAll();
        }

        private void AdvanceIndex()
        {
            if (_longBreakRequested)
            {
                // An early long break closes the cycle just like the scheduled one.
                _longBreakRequested = false;
                if (_index == 1) NotifyPropertyChanged(nameof(IsLongBreak));
                SetIndex(1);
                return;
            }
            SetIndex(_index >= CycleLength ? 1 : _index + 1);
        }

        private void SetIndex(int index)
        {
            if (_index == index) return;
            _index = index;
            NotifyPropertyChanged(nameof(Index));
            NotifyPropertyChanged(nameof(IsLongBreak));
            NotifyPropertyChanged(nameof(BreakTime));
            NotifyPropertyChanged(nameof(Counter));
        }

        private void SetPhase(PomodoroPhase phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            NotifyPropertyChanged(nameof(Phase));
            NotifyPropertyChanged(nameof(PausedPhase));
            NotifyPropertyChanged(nameof(IsRunning));
            NotifyPropertyChanged(nameof(CurrentIntervalLength));
            NotifyPropertyChanged(nameof(Counter));
            NotifyPropertyChanged(nameof(CanStartLongBreak));
        }

        private void Raise(PomodoroTransitionKind kind, PomodoroPhase from, bool automatic, bool? isLongBreak = null)
        {
            Transitioned?.Invoke(new PomodoroTransition(kind, from, _phase, isLongBreak ?? IsLongBreak, automatic));
        }

        /// <summary>
        /// The timer ticks several times a second so the display never skips a
        /// second; only announce time when the visible value actually changes.
        /// </summary>
        private void NotifyTime(bool force)
        {
            var second = Elapsed;
            if (!force && second == _lastNotifiedSecond) return;
            _lastNotifiedSecond = second;
            NotifyPropertyChanged(nameof(Elapsed));
            NotifyPropertyChanged(nameof(Remaining));
            NotifyPropertyChanged(nameof(DisplayValue));
            NotifyPropertyChanged(nameof(Progress));
        }

        private void NotifyAll()
        {
            NotifyPropertyChanged(nameof(Phase));
            NotifyPropertyChanged(nameof(PausedPhase));
            NotifyPropertyChanged(nameof(IsRunning));
            NotifyPropertyChanged(nameof(Index));
            NotifyPropertyChanged(nameof(IsLongBreak));
            NotifyPropertyChanged(nameof(WorkTime));
            NotifyPropertyChanged(nameof(BreakTime));
            NotifyPropertyChanged(nameof(CurrentIntervalLength));
            NotifyPropertyChanged(nameof(Counter));
            NotifyTime(force: true);
        }

        private void OnGlobalSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            var name = e.PropertyName ?? string.Empty;
            if (!name.StartsWith(nameof(PomodoroEngine) + ".", StringComparison.Ordinal)) return;

            switch (name.Substring(nameof(PomodoroEngine).Length + 1))
            {
                case nameof(PomodoroEngineSettings.Counter):
                    NotifyPropertyChanged(nameof(Counter));
                    break;
                case nameof(PomodoroEngineSettings.Profiles):
                case nameof(PomodoroEngineSettings.ActiveProfile):
                case nameof(PomodoroEngineSettings.CountBackwards):
                    NotifyAll();
                    break;
            }
        }

        private void NotifyPropertyChanged(string property)
        {
            _threading.RunOnUiThread(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)));
        }
    }
}
