using System;
using System.Threading.Tasks;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Tools;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Tools
{
    // The open app keeps checking for updates: each tick asks for an automatic check, which the provider spaces out.
    public class UpdateWatcherTests
    {
        [Fact]
        public async Task Each_tick_asks_for_an_automatic_check()
        {
            var updates = new RecordingUpdateProvider();
            using var watcher = new UpdateWatcher(updates, new FakeApplicationLogger());

            await watcher.TickAsync();
            await watcher.TickAsync();

            Assert.Equal(new[] { false, false }, updates.Checks);
        }

        [Fact]
        public async Task A_failing_check_is_logged_not_thrown()
        {
            var updates = new RecordingUpdateProvider { Throw = true };
            var logger = new FakeApplicationLogger();
            using var watcher = new UpdateWatcher(updates, logger);

            await watcher.TickAsync();

            Assert.Contains(logger.Messages, m => m.Contains("no network"));
        }

        [Fact]
        public void Ticks_often_enough_to_honour_the_hourly_check()
        {
            Assert.True(UpdateWatcher.TickInterval < CoreConstants.UpdateCheckInterval);
            Assert.Equal(TimeSpan.FromHours(1), CoreConstants.UpdateCheckInterval);
        }

        private sealed class RecordingUpdateProvider : ISoftwareUpdateProvider
        {
            public System.Collections.Generic.List<bool> Checks { get; } = new();
            public bool Throw { get; set; }

            public event EventHandler? UpdateCheckStarted { add { } remove { } }
            public event EventHandler<SoftwareUpdateEventArgs>? UpdateCheckFinished { add { } remove { } }
            public SoftwareUpdateEventArgs? LastResult => null;

            public Task CheckForUpdatesAsync(bool isManualCheck)
            {
                Checks.Add(isManualCheck);
                return Throw ? Task.FromException(new InvalidOperationException("no network")) : Task.CompletedTask;
            }
        }
    }
}
