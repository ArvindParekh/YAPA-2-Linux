using System;

namespace YAPA.Shared.Contracts
{
    public interface IClock
    {
        /// <summary>Wall-clock time, for timestamps that are shown to people or stored.</summary>
        DateTime UtcNow { get; }

        /// <summary>
        /// Monotonic time for measuring durations. On Linux this is CLOCK_MONOTONIC,
        /// which stops while the machine is suspended, so a pomodoro left running
        /// over a closed laptop lid doesn't silently "complete" while nobody worked.
        /// </summary>
        TimeSpan Monotonic { get; }
    }
}
