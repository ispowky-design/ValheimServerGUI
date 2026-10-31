using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Game.Network;
using ValheimServerGUI.Localization;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.Network
{
    public class ConnectionDiagnosticsTests
    {
        private sealed class FakeProbe : IConnectionProbe
        {
            public HashSet<int> PortsInUse { get; } = new();
            public bool Answers { get; set; } = true;
            public IPEndPoint? QueriedEndpoint { get; private set; }
            public FirewallState Firewall { get; set; } = FirewallState.Allowed;
            public IReadOnlyList<int>? SteamPorts { get; set; } = new[] { 2456 };
            public string? SteamAskedFor { get; private set; }

            public bool IsUdpPortInUse(int port) => PortsInUse.Contains(port);

            public Task<bool> ServerAnswersQueryAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancel)
            {
                QueriedEndpoint = endpoint;
                return Task.FromResult(Answers);
            }

            public FirewallState GetFirewallState(int firstPort, int lastPort, string? serverExePath) => Firewall;

            public Task<IReadOnlyList<int>?> GetSteamListedGamePortsAsync(string publicIp, CancellationToken cancel)
            {
                SteamAskedFor = publicIp;
                return Task.FromResult(SteamPorts);
            }

            public Task<bool> AddFirewallRuleAsync(int firstPort, int lastPort) => Task.FromResult(true);
        }

        private static ConnectionDiagnosticInput Input(bool running = true, bool crossplay = false, string? ip = "83.198.250.191")
            => new(2456, crossplay, Public: true, running, null, ip, "192.168.1.33");

        private static DiagnosticResult Get(IReadOnlyList<DiagnosticResult> results, ConnectionCheck check)
            => Assert.Single(results, r => r.Check == check);

        [Fact]
        public async Task AllGood_WhileRunning()
        {
            var probe = new FakeProbe();
            var results = await new ConnectionDiagnostics(probe).RunAsync(Input());

            Assert.DoesNotContain(results, r => r.Outcome is DiagnosticOutcome.Warning or DiagnosticOutcome.Problem);
            Assert.Equal(DiagnosticOutcome.Ok, Get(results, ConnectionCheck.SteamListing).Outcome);
            Assert.Equal(DiagnosticOutcome.Skipped, Get(results, ConnectionCheck.PortsFree).Outcome);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, 2457), probe.QueriedEndpoint);
            Assert.Equal("83.198.250.191", probe.SteamAskedFor);
        }

        [Fact]
        public async Task Stopped_ChecksThePorts_AndSkipsTheServerChecks()
        {
            var probe = new FakeProbe();
            probe.PortsInUse.Add(2457);
            var results = await new ConnectionDiagnostics(probe).RunAsync(Input(running: false));

            var ports = Get(results, ConnectionCheck.PortsFree);
            Assert.Equal(DiagnosticOutcome.Problem, ports.Outcome);
            Assert.Equal(string.Format(Strings.Diagnostic_Ports_Taken, "2457"), ports.Detail);
            Assert.Equal(DiagnosticOutcome.Skipped, Get(results, ConnectionCheck.ServerAnswers).Outcome);
            Assert.Equal(DiagnosticOutcome.Skipped, Get(results, ConnectionCheck.SteamListing).Outcome);
            Assert.Null(probe.QueriedEndpoint);
            Assert.Null(probe.SteamAskedFor);
        }

        [Fact]
        public async Task NotSeenBySteam_AsksToForwardThePorts()
        {
            var probe = new FakeProbe { SteamPorts = Array.Empty<int>() };
            var result = Get(await new ConnectionDiagnostics(probe).RunAsync(Input()), ConnectionCheck.SteamListing);

            Assert.Equal(DiagnosticOutcome.Warning, result.Outcome);
            Assert.Equal(string.Format(Strings.Diagnostic_Steam_NotListed, string.Format(Strings.Diagnostic_PortRange, 2456, 2457), "192.168.1.33"),
                result.Detail);
        }

        [Fact]
        public async Task Crossplay_IsExplained_AndAMissingSteamListingIsOnlyInformation()
        {
            var probe = new FakeProbe { SteamPorts = Array.Empty<int>() };
            var results = await new ConnectionDiagnostics(probe).RunAsync(Input(crossplay: true));

            Assert.Equal(ConnectionCheck.Crossplay, results[0].Check);
            Assert.Equal(DiagnosticOutcome.Info, Get(results, ConnectionCheck.SteamListing).Outcome);
        }

        [Theory]
        [InlineData(FirewallState.Allowed, DiagnosticOutcome.Ok, DiagnosticFix.None)]
        [InlineData(FirewallState.Off, DiagnosticOutcome.Ok, DiagnosticFix.None)]
        [InlineData(FirewallState.NoRule, DiagnosticOutcome.Warning, DiagnosticFix.AddFirewallRule)]
        [InlineData(FirewallState.Blocked, DiagnosticOutcome.Problem, DiagnosticFix.AddFirewallRule)]
        [InlineData(FirewallState.Unknown, DiagnosticOutcome.Skipped, DiagnosticFix.None)]
        public async Task Firewall_Verdicts(FirewallState state, DiagnosticOutcome outcome, DiagnosticFix fix)
        {
            var result = Get(await new ConnectionDiagnostics(new FakeProbe { Firewall = state }).RunAsync(Input()), ConnectionCheck.Firewall);

            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(fix, result.Fix);
        }

        [Fact]
        public async Task SharedAddress_IsAProblem_AndSteamIsNotAsked()
        {
            var probe = new FakeProbe();
            var results = await new ConnectionDiagnostics(probe).RunAsync(Input(ip: "100.70.1.2"));

            Assert.Equal(DiagnosticOutcome.Problem, Get(results, ConnectionCheck.PublicAddress).Outcome);
            Assert.Equal(DiagnosticOutcome.Skipped, Get(results, ConnectionCheck.SteamListing).Outcome);
            Assert.Null(probe.SteamAskedFor);
        }

        [Fact]
        public async Task UnknownAddress_IsAWarning()
        {
            var results = await new ConnectionDiagnostics(new FakeProbe()).RunAsync(Input(ip: null));

            Assert.Equal(DiagnosticOutcome.Warning, Get(results, ConnectionCheck.PublicAddress).Outcome);
        }

        [Fact]
        public async Task SilentServer_IsAWarning()
        {
            var result = Get(await new ConnectionDiagnostics(new FakeProbe { Answers = false }).RunAsync(Input()), ConnectionCheck.ServerAnswers);

            Assert.Equal(DiagnosticOutcome.Warning, result.Outcome);
        }

        [Theory]
        [InlineData("*", true)]
        [InlineData("2456-2458", true)]
        [InlineData("2456,2457", true)]
        [InlineData("2456", false)]
        [InlineData("2400-2456, 2457", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void PortsCover_ReadsRulePortText(string? text, bool covers)
            => Assert.Equal(covers, ConnectionProbe.PortsCover(text, 2456, 2457));

        [Fact]
        public void ParseSteamGamePorts_KeepsValheimServersOnly()
        {
            const string json = """
                {"response":{"success":true,"servers":[
                  {"addr":"83.198.250.191:2457","appid":892970,"gamedir":"valheim","gameport":2456},
                  {"addr":"83.198.250.191:27016","appid":730,"gamedir":"csgo","gameport":27015}]}}
                """;
            Assert.Equal(new[] { 2456 }, ConnectionProbe.ParseSteamGamePorts(json));
            Assert.Null(ConnectionProbe.ParseSteamGamePorts("""{"response":{"success":false}}"""));
            Assert.Empty(ConnectionProbe.ParseSteamGamePorts("""{"response":{"success":true}}""")!);
        }

        [Theory]
        [InlineData("10.0.0.1", true)]
        [InlineData("172.20.1.1", true)]
        [InlineData("192.168.1.33", true)]
        [InlineData("100.64.0.1", true)]
        [InlineData("100.128.0.1", false)]
        [InlineData("83.198.250.191", false)]
        public void IsSharedOrPrivate(string ip, bool shared)
            => Assert.Equal(shared, ConnectionDiagnostics.IsSharedOrPrivate(IPAddress.Parse(ip)));
    }
}
