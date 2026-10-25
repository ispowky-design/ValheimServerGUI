using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Game;

namespace ValheimServerGUI.App.Tests.Fakes;

/// <summary>
/// An <see cref="ISteamCmdService"/> that never runs SteamCMD: an install writes the server exe and an app manifest
/// with <see cref="InstallBuild"/>, reports one progress line, and can be made to fail or to wait for cancellation.
/// </summary>
internal sealed class FakeSteamCmdService : ISteamCmdService
{
    public FakeSteamCmdService(string root) => DefaultInstallFolder = Path.Join(root, "dedicated-server");

    public string SteamCmdFolder => "/fake/steamcmd";
    public string DefaultInstallFolder { get; }
    public string InstallBuild { get; set; } = "100";
    public string? LatestBuild { get; set; } = "100";
    public Exception? Failure { get; set; }
    public bool WaitForCancel { get; set; }
    public List<(string Folder, bool Validate)> Installs { get; } = new();

    public string? FindSteamCmd() => "/fake/steamcmd/steamcmd.sh";

    public Task<string> DownloadSteamCmdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
        => Task.FromResult(FindSteamCmd()!);

    public async Task InstallOrUpdateServerAsync(string installFolder, bool validate, IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
    {
        Installs.Add((installFolder, validate));
        progress?.Report(new SteamCmdProgress("Update state (0x61) downloading, progress: 50.00", 50));
        if (WaitForCancel) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Failure is not null) throw Failure;

        Directory.CreateDirectory(Path.Join(installFolder, "steamapps"));
        File.WriteAllText(GetServerExePath(installFolder), "");
        File.WriteAllText(Path.Join(installFolder, "steamapps", "appmanifest_896660.acf"), $"\"buildid\" \"{InstallBuild}\"");
    }

    public string? GetInstalledBuildId(string installFolder)
    {
        var manifest = Path.Join(installFolder, "steamapps", "appmanifest_896660.acf");
        return File.Exists(manifest) ? InstallBuild : null;
    }

    public Task<string?> GetLatestBuildIdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
        => Task.FromResult(LatestBuild);

    public string GetServerExePath(string installFolder) => Path.Join(installFolder, "valheim_server.x86_64");
}
