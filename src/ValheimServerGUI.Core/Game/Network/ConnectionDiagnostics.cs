using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game.Network
{
    /// <summary>The checks the connection diagnostic runs, in the order it shows them.</summary>
    public enum ConnectionCheck
    {
        Crossplay,
        PortsFree,
        ServerAnswers,
        Firewall,
        PublicAddress,
        SteamListing,
    }

    public enum DiagnosticOutcome
    {
        Ok,
        Info,
        Warning,
        Problem,
        Skipped,
    }

    /// <summary>A fix the diagnostic can apply itself.</summary>
    public enum DiagnosticFix
    {
        None,
        AddFirewallRule,
    }

    /// <param name="Detail">User-facing explanation, from <see cref="Strings"/>.</param>
    public sealed record DiagnosticResult(ConnectionCheck Check, DiagnosticOutcome Outcome, string Detail, DiagnosticFix Fix = DiagnosticFix.None);

    /// <summary>What the diagnostic needs to know about the profile being tested.</summary>
    public sealed record ConnectionDiagnosticInput(
        int Port,
        bool Crossplay,
        bool Public,
        bool ServerRunning,
        string? ServerExePath,
        string? ExternalIp,
        string? InternalIp);

    /// <summary>The state of the Windows firewall for the server.</summary>
    public enum FirewallState
    {
        Unknown,
        Off,
        Allowed,
        Blocked,
        NoRule,
    }

    /// <summary>The probes the diagnostic uses; real network and OS calls in <see cref="ConnectionProbe"/>.</summary>
    public interface IConnectionProbe
    {
        /// <summary>Whether a local program (any) listens on the UDP port.</summary>
        bool IsUdpPortInUse(int port);

        /// <summary>Whether a Steam query (A2S_INFO) to the endpoint gets any answer within the timeout.</summary>
        Task<bool> ServerAnswersQueryAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancel);

        /// <summary>The firewall's verdict for inbound UDP on the ports, or for the server exe.</summary>
        FirewallState GetFirewallState(int firstPort, int lastPort, string? serverExePath);

        /// <summary>The game ports Steam's master server lists at the public IP, or null if Steam could not be asked.</summary>
        Task<IReadOnlyList<int>?> GetSteamListedGamePortsAsync(string publicIp, CancellationToken cancel);

        /// <summary>The one change it can make, on the user's click: an inbound "allow UDP" firewall rule (Windows).</summary>
        Task<bool> AddFirewallRuleAsync(int firstPort, int lastPort);
    }

    /// <summary>
    /// "Can my friends join?": checks, without changing anything, what usually stops players reaching a self-hosted
    /// server (ports taken, the server not answering, the firewall, the public address, the router not forwarding).
    /// Valheim uses UDP <c>port</c> (game) and <c>port + 1</c> (Steam queries).
    /// </summary>
    public sealed class ConnectionDiagnostics
    {
        public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

        private readonly IConnectionProbe _probe;

        public ConnectionDiagnostics(IConnectionProbe probe)
        {
            _probe = probe;
        }

        public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(ConnectionDiagnosticInput input, CancellationToken cancel = default)
        {
            var results = new List<DiagnosticResult>();
            var port = input.Port;
            var queryPort = port + 1;
            var range = string.Format(Strings.Diagnostic_PortRange, port, queryPort);

            if (input.Crossplay)
                results.Add(new(ConnectionCheck.Crossplay, DiagnosticOutcome.Info, Strings.Diagnostic_Crossplay_On));

            // Ports: only meaningful while stopped; a running server holds them itself.
            if (input.ServerRunning)
            {
                results.Add(new(ConnectionCheck.PortsFree, DiagnosticOutcome.Skipped, Strings.Diagnostic_Ports_Running));
            }
            else
            {
                var taken = new[] { port, queryPort }.Where(_probe.IsUdpPortInUse).ToList();
                results.Add(taken.Count == 0
                    ? new(ConnectionCheck.PortsFree, DiagnosticOutcome.Ok, string.Format(Strings.Diagnostic_Ports_Free, range))
                    : new(ConnectionCheck.PortsFree, DiagnosticOutcome.Problem,
                        string.Format(Strings.Diagnostic_Ports_Taken, string.Join(", ", taken))));
            }

            // The server itself, asked locally.
            if (!input.ServerRunning)
            {
                results.Add(new(ConnectionCheck.ServerAnswers, DiagnosticOutcome.Skipped, Strings.Diagnostic_Server_Stopped));
            }
            else
            {
                var answers = await _probe.ServerAnswersQueryAsync(new IPEndPoint(IPAddress.Loopback, queryPort), QueryTimeout, cancel);
                results.Add(answers
                    ? new(ConnectionCheck.ServerAnswers, DiagnosticOutcome.Ok, Strings.Diagnostic_Server_Answers)
                    : new(ConnectionCheck.ServerAnswers, DiagnosticOutcome.Warning,
                        string.Format(Strings.Diagnostic_Server_Silent, queryPort)));
            }

            results.Add(FirewallResult(_probe.GetFirewallState(port, queryPort, input.ServerExePath), range));

            // Public address.
            var publicIp = input.ExternalIp;
            if (string.IsNullOrWhiteSpace(publicIp) || !IPAddress.TryParse(publicIp, out var publicAddress))
            {
                results.Add(new(ConnectionCheck.PublicAddress, DiagnosticOutcome.Warning, Strings.Diagnostic_PublicIp_Unknown));
                publicIp = null;
            }
            else if (IsSharedOrPrivate(publicAddress))
            {
                results.Add(new(ConnectionCheck.PublicAddress, DiagnosticOutcome.Problem,
                    string.Format(Strings.Diagnostic_PublicIp_Shared, publicIp)));
                publicIp = null;
            }
            else
            {
                results.Add(new(ConnectionCheck.PublicAddress, DiagnosticOutcome.Info,
                    string.Format(Strings.Diagnostic_PublicIp_Value, publicIp, port)));
            }

            // Seen from the internet, by Steam.
            if (!input.ServerRunning)
            {
                results.Add(new(ConnectionCheck.SteamListing, DiagnosticOutcome.Skipped, Strings.Diagnostic_Server_Stopped));
            }
            else if (publicIp is null)
            {
                results.Add(new(ConnectionCheck.SteamListing, DiagnosticOutcome.Skipped, Strings.Diagnostic_Steam_NoPublicIp));
            }
            else
            {
                var listed = await _probe.GetSteamListedGamePortsAsync(publicIp, cancel);
                var forwardTo = string.IsNullOrWhiteSpace(input.InternalIp) ? Strings.Diagnostic_ThisPc : input.InternalIp;
                if (listed is null)
                    results.Add(new(ConnectionCheck.SteamListing, DiagnosticOutcome.Warning, Strings.Diagnostic_Steam_Unreachable));
                else if (listed.Contains(port))
                    results.Add(new(ConnectionCheck.SteamListing, DiagnosticOutcome.Ok,
                        string.Format(Strings.Diagnostic_Steam_Listed, publicIp, port)));
                else
                    results.Add(new(ConnectionCheck.SteamListing, input.Crossplay ? DiagnosticOutcome.Info : DiagnosticOutcome.Warning,
                        string.Format(input.Public ? Strings.Diagnostic_Steam_NotListed : Strings.Diagnostic_Steam_NotListedPrivate,
                            range, forwardTo)));
            }

            return results;
        }

        private static DiagnosticResult FirewallResult(FirewallState state, string range) => state switch
        {
            FirewallState.Allowed => new(ConnectionCheck.Firewall, DiagnosticOutcome.Ok, Strings.Diagnostic_Firewall_Allowed),
            FirewallState.Off => new(ConnectionCheck.Firewall, DiagnosticOutcome.Ok, Strings.Diagnostic_Firewall_Off),
            FirewallState.Blocked => new(ConnectionCheck.Firewall, DiagnosticOutcome.Problem, Strings.Diagnostic_Firewall_Blocked, DiagnosticFix.AddFirewallRule),
            FirewallState.NoRule => new(ConnectionCheck.Firewall, DiagnosticOutcome.Warning,
                string.Format(Strings.Diagnostic_Firewall_NoRule, range), DiagnosticFix.AddFirewallRule),
            _ => new(ConnectionCheck.Firewall, DiagnosticOutcome.Skipped, Strings.Diagnostic_Firewall_Unknown),
        };

        /// <summary>
        /// A "public" address that is not really one: private ranges, or the 100.64.0.0/10 block internet providers use
        /// for carrier-grade NAT, where incoming connections never reach the box.
        /// </summary>
        public static bool IsSharedOrPrivate(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || IPAddress.IsLoopback(address);
        }
    }
}
