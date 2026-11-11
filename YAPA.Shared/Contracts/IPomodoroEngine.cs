using System;
using System.ComponentModel;
using YAPA.Shared.Common;

namespace YAPA.Shared.Contracts
{
    public interface IPomodoroEngine : INotifyPropertyChanged
    {
        /// <summary>1-based position in the long-break cycle.</summary>
        int Index { get; }

        /// <summary>1-based: which long-break cycle of the (local) day this is.</summary>
        int Cycle { get; }
        int Counter { get; }
        PomodoroPhase Phase { get; }

        /// <summary>Which phase is suspended while <see cref="Phase"/> is <see cref="PomodoroPhase.Pause"/>.</summary>
        PomodoroPhase PausedPhase { get; }

        int Elapsed { get; }
        int Remaining { get; }

        /// <summary>
        /// Seconds since the interval ran out, while it waits for the next one to be started
        /// (WorkEnded, BreakEnded); null in every other phase.
        /// </summary>
        int? Overrun { get; }
        int DisplayValue { get; }
        int CurrentIntervalLength { get; }

        /// <summary>0..1 through the current interval.</summary>
        double Progress { get; }
        int WorkTime { get; }
        int BreakTime { get; }

        /// <summary>True when the break after the current pomodoro is the long one.</summary>
        bool IsLongBreak { get; }

        bool IsRunning { get; }

        /// <summary>
        /// Monotonic time (<see cref="IClock.Monotonic"/>) at which the displayed time next
        /// changes, or null when nothing is running. Every later change is a whole number of
        /// seconds after it, so this one value is enough to keep anything in step with the
        /// display (the ticking clock uses it).
        /// </summary>
        TimeSpan? NextSecondAt { get; }

        /// <summary>
        /// Raised synchronously after every state change, on the thread that caused it.
        /// Side effects (sounds, notifications, history) hang off this rather than
        /// off <see cref="Phase"/> changes, because the same phase can be reached
        /// in ways that mean different things (start vs resume, finish vs stop).
        /// </summary>
        event Action<PomodoroTransition> Transitioned;

        /// <summary>Invoked when a work interval runs to completion.</summary>
        event Action OnPomodoroCompleted;

        /// <summary>Starts the next interval or resumes a paused one. No-op while running.</summary>
        void Start();
        void Pause();

        /// <summary>Abandons the current work interval, or ends the break early.</summary>
        void Stop();

        /// <summary>Skips the (remaining) break and starts the next pomodoro immediately.</summary>
        void Skip();

        /// <summary>
        /// Takes the long break now instead of waiting for the end of the cycle:
        /// from idle, after work, during or after a short break. When it ends the
        /// cycle starts over at pomodoro 1. No-op during work.
        /// </summary>
        void StartLongBreak();

        bool CanStartLongBreak { get; }

        /// <summary>Back to the first pomodoro of the cycle.</summary>
        void Reset();

        PomodoroEngineSnapshot GetSnapshot();
        void LoadSnapshot(PomodoroEngineSnapshot snapshot);
    }

    public enum PomodoroPhase
    {
        NotStarted,
        Work,
        WorkEnded,
        Break,
        BreakEnded,
        Pause
    }

    public enum CounterEnum
    {
        PomodoroIndex,
        CompletedToday,
        CompletedThisSession
    }

    public enum PomodoroTransitionKind
    {
        WorkStarted,
        BreakStarted,
        Paused,
        Resumed,
        WorkCompleted,
        BreakCompleted,
        Stopped,
        Reset,
        Restored
    }

    public sealed class PomodoroTransition
    {
        public PomodoroTransition(PomodoroTransitionKind kind, PomodoroPhase from, PomodoroPhase to, bool isLongBreak, bool automatic)
        {
            Kind = kind;
            From = from;
            To = to;
            IsLongBreak = isLongBreak;
            Automatic = automatic;
        }

        public PomodoroTransitionKind Kind { get; }
        public PomodoroPhase From { get; }
        public PomodoroPhase To { get; }

        /// <summary>For break-related transitions: whether it is the long break.</summary>
        public bool IsLongBreak { get; }

        /// <summary>True when the engine did this on its own (auto-start), not the user.</summary>
        public bool Automatic { get; }

        public override string ToString() => $"{Kind} ({From} -> {To}{(Automatic ? ", auto" : "")})";
    }
}
