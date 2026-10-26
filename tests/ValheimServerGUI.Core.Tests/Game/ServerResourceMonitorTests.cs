using System;
using ValheimServerGUI.Game;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game
{
    public class ServerResourceMonitorTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        private static ProcessUsageSample Sample(double cpuSeconds, long memory, double atSeconds)
            => new(TimeSpan.FromSeconds(cpuSeconds), memory, T0.AddSeconds(atSeconds));

        [Fact]
        public void First_sample_primes_with_zero_cpu_and_reports_memory()
        {
            var monitor = new ServerResourceMonitor(processorCount: 4);
            var usage = monitor.Next(Sample(10, 500, 0));
            Assert.Equal(new ServerResourceUsage(0, 500), usage);
        }

        [Fact]
        public void Cpu_is_cpu_time_over_wall_time_per_processor()
        {
            var monitor = new ServerResourceMonitor(processorCount: 4);
            monitor.Next(Sample(10, 500, 0));

            // One full core busy for 2s on a 4-processor machine is 25%.
            var usage = monitor.Next(Sample(12, 600, 2));
            Assert.Equal(25, usage!.Value.CpuPercent, precision: 6);
            Assert.Equal(600, usage.Value.MemoryBytes);
        }

        [Fact]
        public void Cpu_is_clamped_to_100()
        {
            var monitor = new ServerResourceMonitor(processorCount: 1);
            monitor.Next(Sample(0, 1, 0));
            Assert.Equal(100, monitor.Next(Sample(5, 1, 1))!.Value.CpuPercent);
        }

        [Fact]
        public void No_process_resets_so_the_next_sample_primes_again()
        {
            var monitor = new ServerResourceMonitor(processorCount: 1);
            monitor.Next(Sample(0, 1, 0));
            Assert.Null(monitor.Next(null));

            // A new process starts its CPU time from zero: no negative or bogus delta.
            Assert.Equal(0, monitor.Next(Sample(0.1, 1, 10))!.Value.CpuPercent);
        }

        [Fact]
        public void Clock_going_backwards_reads_zero()
        {
            var monitor = new ServerResourceMonitor(processorCount: 1);
            monitor.Next(Sample(0, 1, 5));
            Assert.Equal(0, monitor.Next(Sample(1, 1, 4))!.Value.CpuPercent);
        }
    }
}
