using System;

namespace YAPA.Shared.Contracts
{
    public interface ITimer
    {
        event Action Tick;
        void Start();
        void Stop();

        /// <summary>
        /// Asks for the next tick after <paramref name="delay"/> instead of the default
        /// interval. The engine uses it to land the tick right on the next second, so the
        /// display flips when the second actually starts rather than up to a polling
        /// interval later. Timers that can't schedule ignore it.
        /// </summary>
        void ScheduleNext(TimeSpan delay) { }
    }
}
