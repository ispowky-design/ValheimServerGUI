using System;

namespace ValheimServerGUI.Game
{
    /// <summary>A raw reading of the server process: CPU time used so far and its working set, taken at <see cref="At"/>.</summary>
    public readonly record struct ProcessUsageSample(TimeSpan CpuTime, long MemoryBytes, DateTimeOffset At);

    /// <summary>The server's resource use between two samples: CPU as a share of the whole machine (0-100) and memory.</summary>
    public readonly record struct ServerResourceUsage(double CpuPercent, long MemoryBytes);

    /// <summary>
    /// Turns successive <see cref="ProcessUsageSample"/>s into <see cref="ServerResourceUsage"/>. CPU is the process's
    /// CPU time over the wall time between two samples, divided by the processor count, so a server busy on one core
    /// of eight reads 12.5%. The first sample after a reset only primes the monitor (CPU reads 0).
    /// </summary>
    public sealed class ServerResourceMonitor
    {
        private readonly int _processorCount;
        private ProcessUsageSample? _last;

        public ServerResourceMonitor(int? processorCount = null)
        {
            _processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
        }

        /// <summary>Feeds the next sample; a null sample (no process) resets the monitor and returns null.</summary>
        public ServerResourceUsage? Next(ProcessUsageSample? sample)
        {
            if (sample is not { } current)
            {
                _last = null;
                return null;
            }

            var previous = _last;
            _last = current;
            if (previous is not { } last) return new ServerResourceUsage(0, current.MemoryBytes);

            var wall = (current.At - last.At).TotalMilliseconds;
            var cpu = (current.CpuTime - last.CpuTime).TotalMilliseconds;
            if (wall <= 0 || cpu < 0) return new ServerResourceUsage(0, current.MemoryBytes);

            var percent = cpu / wall / _processorCount * 100;
            return new ServerResourceUsage(Math.Clamp(percent, 0, 100), current.MemoryBytes);
        }

        public void Reset() => _last = null;
    }
}
