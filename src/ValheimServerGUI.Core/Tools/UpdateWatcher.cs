using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Tools
{
    /// <summary>
    /// Finds a new release while the app is open, within seconds of its publication. Two ways, so either can fail:
    /// <list type="bullet">
    /// <item>It stays connected to <see cref="CoreConstants.UrlReleasePushTopic"/>, where the release workflow posts
    /// each new version; a message runs a full check at once.</item>
    /// <item>Every <see cref="TickInterval"/> it asks for an automatic check, which <see cref="ISoftwareUpdateProvider"/>
    /// spaces by <see cref="CoreConstants.UpdateCheckInterval"/> and keeps cheap (a look at the latest tag).</item>
    /// </list>
    /// </summary>
    public sealed class UpdateWatcher : IDisposable
    {
        internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

        private readonly ISoftwareUpdateProvider _updates;
        private readonly IHttpClientProvider _http;
        private readonly IApplicationLogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private Timer? _timer;

        public UpdateWatcher(ISoftwareUpdateProvider updates, IHttpClientProvider http, IApplicationLogger logger)
        {
            _updates = updates;
            _http = http;
            _logger = logger;
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);
            _ = ListenAsync(_stop.Token);
        }

        internal async Task TickAsync()
        {
            try
            {
                await _updates.CheckForUpdatesAsync(isManualCheck: false);
            }
            catch (Exception e)
            {
                _logger.Warning("Periodic update check failed: {message}", e.Message);
            }
        }

        // ntfy streams one JSON object per line: "open" when connected, "keepalive" now and then, "message" for a post.
        private async Task ListenAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var client = _http.CreateClient();
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    using var response = await client.GetAsync(CoreConstants.UrlReleasePushTopic + "/json", HttpCompletionOption.ResponseHeadersRead, stop);
                    response.EnsureSuccessStatusCode();
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop));
                    while (await reader.ReadLineAsync(stop) is { } line)
                        await OnPushLineAsync(line);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.Debug("Release notifications disconnected: {message}", e.Message);
                }

                try
                {
                    await Task.Delay(ReconnectDelay, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Runs a check right away when <paramref name="line"/> is a posted message.</summary>
        internal async Task OnPushLineAsync(string line)
        {
            string? kind;
            try
            {
                kind = JObject.Parse(line).Value<string>("event");
            }
            catch (Exception)
            {
                return;
            }
            if (kind != "message") return;

            _logger.Information("A new release was announced; checking for updates");
            try
            {
                await _updates.CheckForUpdatesAsync(isManualCheck: false, releasePublished: true);
            }
            catch (Exception e)
            {
                _logger.Warning("Update check after a release announcement failed: {message}", e.Message);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
