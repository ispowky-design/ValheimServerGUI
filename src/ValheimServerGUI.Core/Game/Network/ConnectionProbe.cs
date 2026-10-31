using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game.Network
{
    /// <summary>The real probes behind <see cref="ConnectionDiagnostics"/>. Every probe only reads; nothing is changed.</summary>
    public sealed class ConnectionProbe : IConnectionProbe
    {
        // Steam's public Web API: the game servers its master server knows at an address. Needs no key.
        private const string SteamServersAtAddressUrl = "https://api.steampowered.com/ISteamApps/GetServersAtAddress/v1/?addr=";

        // A2S_INFO: 0xFFFFFFFF, 'T', "Source Engine Query\0".
        private static readonly byte[] InfoQuery =
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 }.Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();

        private readonly IHttpClientProvider _http;
        private readonly IApplicationLogger _logger;

        public ConnectionProbe(IHttpClientProvider http, IApplicationLogger logger)
        {
            _http = http;
            _logger = logger;
        }

        public bool IsUdpPortInUse(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == port);
            }
            catch (NetworkInformationException ex)
            {
                _logger.Warning(ex, "Could not list the UDP listeners: {message}", ex.Message);
                return false;
            }
        }

        public async Task<bool> ServerAnswersQueryAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancel)
        {
            using var udp = new UdpClient(endpoint.AddressFamily);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try
            {
                await udp.SendAsync(InfoQuery, endpoint, timer.Token);
                // Any answer counts: the server replies with its info, or with a challenge to repeat the query.
                var reply = await udp.ReceiveAsync(timer.Token);
                return reply.Buffer.Length >= 5;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<int>?> GetSteamListedGamePortsAsync(string publicIp, CancellationToken cancel)
        {
            try
            {
                using var client = _http.CreateIPv4Client();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync(SteamServersAtAddressUrl + Uri.EscapeDataString(publicIp), cancel);
                return ParseSteamGamePorts(json);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                _logger.Warning(ex, "Could not ask Steam which servers it sees at the public address: {message}", ex.Message);
                return null;
            }
        }

        /// <summary>The Valheim game ports in a GetServersAtAddress answer, or null when Steam says it failed.</summary>
        public static IReadOnlyList<int>? ParseSteamGamePorts(string json)
        {
            var response = JObject.Parse(json)["response"];
            if (response?["success"]?.Value<bool>() != true) return null;

            return (response["servers"] as JArray ?? new JArray())
                .Where(s => s["appid"]?.Value<int>() == int.Parse(CoreConstants.ValheimSteamAppId))
                .Select(s => s["gameport"]?.Value<int>() ?? 0)
                .Where(p => p > 0)
                .ToList();
        }

        public FirewallState GetFirewallState(int firstPort, int lastPort, string? serverExePath)
        {
            if (!OperatingSystem.IsWindows()) return FirewallState.Unknown;
            try
            {
                return ReadWindowsFirewall(firstPort, lastPort, serverExePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not read the Windows firewall rules: {message}", ex.Message);
                return FirewallState.Unknown;
            }
        }

        // Reads the rules through the firewall's COM API (HNetCfg.FwPolicy2), which needs no elevation. Constants:
        // direction 1 = in; action 0 = block, 1 = allow; protocol 17 = UDP, 256 = any.
        [SupportedOSPlatform("windows")]
        private static FirewallState ReadWindowsFirewall(int firstPort, int lastPort, string? serverExePath)
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return FirewallState.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;

            int currentProfiles = policy.CurrentProfileTypes;
            var anyOn = false;
            foreach (var profile in new[] { 1, 2, 4 })
            {
                if ((currentProfiles & profile) != 0 && (bool)policy.FirewallEnabled[profile]) anyOn = true;
            }
            if (!anyOn) return FirewallState.Off;

            var allowed = false;
            var blocked = false;
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != 1) continue;
                if (((int)rule.Profiles & currentProfiles) == 0) continue;
                int protocol = rule.Protocol;
                if (protocol != 17 && protocol != 256) continue;

                string? app = rule.ApplicationName;
                string? ports = rule.LocalPorts;
                var matchesApp = !string.IsNullOrEmpty(app) && !string.IsNullOrEmpty(serverExePath)
                                 && string.Equals(Environment.ExpandEnvironmentVariables(app), serverExePath, StringComparison.OrdinalIgnoreCase);
                var matchesPorts = string.IsNullOrEmpty(app) && PortsCover(ports, firstPort, lastPort);
                if (!matchesApp && !matchesPorts) continue;

                if ((int)rule.Action == 0) blocked = true;
                else allowed = true;
            }

            // A block rule wins over an allow rule in Windows Firewall.
            return blocked ? FirewallState.Blocked : allowed ? FirewallState.Allowed : FirewallState.NoRule;
        }

        /// <summary>Whether a rule's LocalPorts text ("*", "2456-2458", "2456,2457") covers every port of the range.</summary>
        public static bool PortsCover(string? localPorts, int firstPort, int lastPort)
        {
            if (string.IsNullOrWhiteSpace(localPorts)) return false;
            if (localPorts.Trim() == "*") return true;

            var covered = new HashSet<int>();
            foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length == 1 && int.TryParse(bounds[0], out var single)) covered.Add(single);
                else if (bounds.Length == 2 && int.TryParse(bounds[0], out var lo) && int.TryParse(bounds[1], out var hi))
                    for (var p = Math.Max(lo, firstPort); p <= Math.Min(hi, lastPort); p++) covered.Add(p);
            }
            return Enumerable.Range(firstPort, lastPort - firstPort + 1).All(covered.Contains);
        }

        /// <summary>
        /// Adds an inbound "allow UDP" rule for the ports, through <c>netsh</c> run elevated (Windows asks the user for
        /// administrator rights). Returns false when the user refused or netsh failed.
        /// </summary>
        public async Task<bool> AddFirewallRuleAsync(int firstPort, int lastPort)
        {
            if (!OperatingSystem.IsWindows()) return false;

            var args = $"advfirewall firewall add rule name=\"Valheim Server ({firstPort}-{lastPort}, VSG)\" dir=in action=allow protocol=UDP localport={firstPort}-{lastPort}";
            _logger.Information("Adding a firewall rule: netsh {args}", args);
            try
            {
                using var process = Process.Start(new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (process is null) return false;
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223: the user said no to the administrator prompt.
                _logger.Warning(ex, "Firewall rule not added: {message}", ex.Message);
                return false;
            }
        }
    }
}
