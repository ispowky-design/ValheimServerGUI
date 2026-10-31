using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game
{
    /// <summary>Posts a message to a Discord channel webhook.</summary>
    public interface IDiscordWebhookClient
    {
        /// <summary>Posts <paramref name="content"/>; false (logged) when Discord refused it or could not be reached.</summary>
        Task<bool> PostAsync(string webhookUrl, string content);
    }

    public sealed class DiscordWebhookClient : IDiscordWebhookClient
    {
        private readonly IHttpClientProvider _http;
        private readonly IApplicationLogger _logger;

        public DiscordWebhookClient(IHttpClientProvider http, IApplicationLogger logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<bool> PostAsync(string webhookUrl, string content)
        {
            // No mentions are ever parsed, so a player named "@everyone" pings nobody.
            var body = JsonConvert.SerializeObject(new { content, allowed_mentions = new { parse = Array.Empty<string>() } });
            try
            {
                using var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var response = await client.PostAsync(webhookUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode) return true;

                _logger.Warning("Discord webhook refused the message: HTTP {status}", (int)response.StatusCode);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.Warning(ex, "Could not post to the Discord webhook: {message}", ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Tells a Discord channel what the servers do: online, restarted, stopped, crashed, and players joining or leaving.
    /// Each server posts to its own profile's webhook (<see cref="IValheimServerOptions.DiscordWebhookUrl"/>), with the
    /// options it was started with. Posting never blocks or fails the server; errors are only logged.
    /// </summary>
    public sealed class DiscordNotifier : IDisposable
    {
        private static readonly Regex WebhookUrl = new(
            @"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/webhooks/\d+/[\w-]+/?$", RegexOptions.IgnoreCase);

        private readonly IServerManager _servers;
        private readonly IPlayerDataRepository _players;
        private readonly IDiscordWebhookClient _client;
        private readonly IApplicationLogger _logger;
        private readonly ConcurrentDictionary<ValheimServer, ServerState> _watched = new();
        private readonly ConcurrentDictionary<string, PlayerStatus> _playerStatus = new();
        private bool _started;

        private sealed class ServerState
        {
            public ServerStatus Last = ServerStatus.Stopped;
            public bool Restarting;
        }

        public DiscordNotifier(IServerManager servers, IPlayerDataRepository players, IDiscordWebhookClient client, IApplicationLogger logger)
        {
            _servers = servers;
            _players = players;
            _client = client;
            _logger = logger;
        }

        /// <summary>Whether the text is a Discord channel webhook address.</summary>
        public static bool IsWebhookUrl(string? url) => url is not null && WebhookUrl.IsMatch(url.Trim());

        /// <summary>Starts listening (idempotent): to every server, now and as they are created, and to players.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;

            foreach (var server in _servers.All) Watch(server);
            _servers.ServerCreated += OnServerCreated;
            _players.PlayerStatusChanged += OnPlayerStatusChanged;
        }

        /// <summary>Posts a test message (the profile's "Send a test" button).</summary>
        public Task<bool> SendTestAsync(string webhookUrl, string? serverName)
            => _client.PostAsync(webhookUrl.Trim(), string.Format(Strings.Discord_Test, Escape(serverName)));

        private void OnServerCreated(object? sender, ValheimServer server) => Watch(server);

        private void Watch(ValheimServer server)
        {
            if (!_watched.TryAdd(server, new ServerState { Last = server.Status })) return;
            server.StatusChanged += OnStatusChanged;
            server.Crashed += OnCrashed;
        }

        private void OnStatusChanged(object? sender, ServerStatus status)
        {
            if (sender is not ValheimServer server || !_watched.TryGetValue(server, out var state)) return;

            var previous = state.Last;
            state.Last = status;
            if (previous == status) return; // a repeated notice (a crash restart becoming pending)

            var options = server.Options;
            if (!options.DiscordNotifyServer) return;

            switch (status)
            {
                case ServerStatus.Running:
                    Post(options, state.Restarting
                        ? string.Format(Strings.Discord_Restarted, Escape(options.Name))
                        : string.Format(Strings.Discord_Online, Escape(options.Name), Escape(options.WorldName)));
                    state.Restarting = false;
                    break;

                // A crash goes from Starting/Running straight to Stopped and is told by OnCrashed instead.
                case ServerStatus.Stopped when previous == ServerStatus.Stopping:
                    if (server.IsRestartInProgress) state.Restarting = true;
                    else Post(options, string.Format(Strings.Discord_Stopped, Escape(options.Name)));
                    break;
            }
        }

        private void OnCrashed(object? sender, ServerCrash crash)
        {
            if (sender is not ValheimServer server) return;

            var options = server.Options;
            if (!options.DiscordNotifyCrashes) return;

            var name = Escape(options.Name);
            string message;
            if (crash.RestartDueAt is { } due)
            {
                message = string.Format(Strings.Discord_Crashed_Restarting, name,
                    Math.Max(0, (int)Math.Round((due - DateTime.Now).TotalSeconds)));
                if (_watched.TryGetValue(server, out var state)) state.Restarting = true;
            }
            else if (options.RestartOnCrash)
            {
                message = string.Format(Strings.Discord_Crashed_GaveUp, name, crash.ConsecutiveCrashes);
            }
            else
            {
                message = string.Format(Strings.Discord_Crashed, name);
            }

            Post(options, message);
        }

        private void OnPlayerStatusChanged(object? sender, PlayerInfo player)
        {
            var previous = _playerStatus.TryGetValue(player.Key, out var p) ? p : PlayerStatus.Offline;
            _playerStatus[player.Key] = player.PlayerStatus;
            if (previous == player.PlayerStatus) return; // a name update, not a join or leave

            string? template = (previous, player.PlayerStatus) switch
            {
                (not PlayerStatus.Online, PlayerStatus.Online) => Strings.Discord_PlayerJoined,
                (PlayerStatus.Online, PlayerStatus.Offline) => Strings.Discord_PlayerLeft,
                _ => null,
            };
            if (template is null) return;

            // The in-game character name players know each other by, else the platform name or id.
            var who = Escape(new[] { player.LastStatusCharacter, player.PlayerName, player.PlayerId }
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));

            // Players are not tied to one server in the app's records, so every running server with player messages on
            // tells its channel (one server: exactly the right one).
            foreach (var server in _servers.All.Where(s => s.Status == ServerStatus.Running))
            {
                if (server.Options.DiscordNotifyPlayers)
                    Post(server.Options, string.Format(template, who, Escape(server.Options.Name)));
            }
        }

        private void Post(IValheimServerOptions options, string message)
        {
            var url = options.DiscordWebhookUrl;
            if (!IsWebhookUrl(url)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.PostAsync(url!.Trim(), message);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Discord notification failed: {message}", ex.Message);
                }
            });
        }

        /// <summary>Escapes Discord markdown in names so a player called "*x*" shows as typed.</summary>
        public static string Escape(string? text)
            => Regex.Replace(text ?? string.Empty, @"([\\*_~`|>#\[\]()])", @"\$1");

        public void Dispose()
        {
            if (!_started) return;
            _servers.ServerCreated -= OnServerCreated;
            _players.PlayerStatusChanged -= OnPlayerStatusChanged;
            foreach (var server in _watched.Keys)
            {
                server.StatusChanged -= OnStatusChanged;
                server.Crashed -= OnCrashed;
            }
            _watched.Clear();
            _started = false;
        }
    }
}
