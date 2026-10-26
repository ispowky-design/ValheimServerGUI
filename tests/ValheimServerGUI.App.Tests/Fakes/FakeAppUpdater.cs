using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Models;

namespace ValheimServerGUI.App.Tests.Fakes;

/// <summary>Installs from any release that lists a file; records downloads and installs instead of doing them.</summary>
internal sealed class FakeAppUpdater : IAppUpdater
{
    public AppInstallKind InstallKind { get; set; } = AppInstallKind.Portable;
    public string? DownloadedVersion { get; private set; }
    public Exception? DownloadError { get; set; }
    public List<string> Downloads { get; } = new();
    public List<string> Installs { get; } = new();

    public event EventHandler<double>? DownloadProgressChanged;

    public ReleaseAsset? FindInstallableAsset(SoftwareUpdateEventArgs update)
        => InstallKind == AppInstallKind.Unsupported ? null : update.Assets.FirstOrDefault();

    public Task<string> DownloadAsync(SoftwareUpdateEventArgs update)
    {
        Downloads.Add(update.LatestVersion!);
        if (DownloadError is { } e) return Task.FromException<string>(e);
        DownloadProgressChanged?.Invoke(this, 1.0);
        DownloadedVersion = update.LatestVersion;
        return Task.FromResult("/downloads/" + update.Assets[0].Name);
    }

    public void LaunchInstall(string downloadedFile) => Installs.Add(downloadedFile);

    public void CleanUpPreviousUpdate()
    {
    }
}
