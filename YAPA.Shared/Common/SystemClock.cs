using System;
using System.Diagnostics;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Common
{
    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public TimeSpan Monotonic => Stopwatch.GetElapsedTime(0);
    }
}
