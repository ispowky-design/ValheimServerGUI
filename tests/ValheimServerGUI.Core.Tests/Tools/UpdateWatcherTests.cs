using System;
using System.Collections.Generic;
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
            using var watcher = new UpdateWatcher(updates, new StubHttpClientProvider(), new FakeApplicationLogger());

            await watcher.TickAsync();
            await watcher.TickAsync();

            Assert.Equal(new[] { false, false }, updates.Checks);
        }

        [Fact]
        public async Task A_failing_check_is_logged_not_thrown()
        {
            var updates = new RecordingUpdateProvider { Throw = true };
            var logger = new FakeApplicationLogger();
            using var watcher = new UpdateWatcher(updates, new StubHttpClientProvider(), logger);

            await watcher.TickAsync();

            Assert.Contains(logger.Messages, m => m.Contains("no network"));
        }

        [Fact]
        public void Ticks_often_enough_to_honour_the_check_interval()
        {
            Assert.True(UpdateWatcher.TickInterval <= CoreConstants.UpdateCheckInterval);
            Assert.True(CoreConstants.UpdateCheckInterval <= TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task An_announced_release_is_checked_at_once()
        {
            var updates = new RecordingUpdateProvider();
            using var watcher = new UpdateWatcher(updates, new StubHttpClientProvider(), new FakeApplicationLogger());

            await watcher.OnPushLineAsync("{\"id\":\"a\",\"event\":\"message\",\"topic\":\"t\",\"message\":\"v3.6.1\"}");

            Assert.Equal(new[] { false }, updates.Checks);
            Assert.Equal(new[] { true }, updates.Published);
        }

        [Theory]
        [InlineData("{\"id\":\"a\",\"event\":\"open\",\"topic\":\"t\"}")]
        [InlineData("{\"id\":\"a\",\"event\":\"keepalive\",\"topic\":\"t\"}")]
        [InlineData("not json")]
        [InlineData("")]
        public async Task Other_stream_lines_are_ignored(string line)
        {
            var updates = new RecordingUpdateProvider();
            using var watcher = new UpdateWatcher(updates, new StubHttpClientProvider(), new FakeApplicationLogger());

            await watcher.OnPushLineAsync(line);

            Assert.Empty(updates.Checks);
        }

        private sealed class RecordingUpdateProvider : ISoftwareUpdateProvider
        {
            public List<bool> Checks { get; } = new();
            public bool Throw { get; set; }

            public event EventHandler? UpdateCheckStarted { add { } remove { } }
            public event EventHandler<SoftwareUpdateEventArgs>? UpdateCheckFinished { add { } remove { } }
            public SoftwareUpdateEventArgs? LastResult => null;

            public List<bool> Published { get; } = new();

            public Task CheckForUpdatesAsync(bool isManualCheck, bool releasePublished = false)
            {
                Checks.Add(isManualCheck);
                Published.Add(releasePublished);
                return Throw ? Task.FromException(new InvalidOperationException("no network")) : Task.CompletedTask;
            }
        }
    }
}
