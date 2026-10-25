using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game;

/// <summary>One step of a SteamCMD run: an output line, and the download/verify percentage when the line carries one.</summary>
public sealed record SteamCmdProgress(string Line, double? Percent);

/// <summary>
/// Installs and updates the Valheim dedicated server with Valve's SteamCMD (anonymous login, app 896660). SteamCMD
/// itself is downloaded on first use into the app's data folder. Callers make sure no server runs from the install
/// folder during an update.
/// </summary>
public interface ISteamCmdService
{
    /// <summary>Where VSG keeps its own copy of SteamCMD.</summary>
    string SteamCmdFolder { get; }

    /// <summary>The suggested server install folder when the user has not picked one.</summary>
    string DefaultInstallFolder { get; }

    /// <summary>The SteamCMD launcher in <see cref="SteamCmdFolder"/>, or null when it is not downloaded yet.</summary>
    string? FindSteamCmd();

    /// <summary>Downloads and unpacks SteamCMD into <see cref="SteamCmdFolder"/>; returns the launcher path.</summary>
    Task<string> DownloadSteamCmdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Installs the dedicated server into <paramref name="installFolder"/>, or updates it in place.</summary>
    Task InstallOrUpdateServerAsync(string installFolder, bool validate, IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The build installed in <paramref name="installFolder"/> (from its app manifest), or null.</summary>
    string? GetInstalledBuildId(string installFolder);

    /// <summary>Asks Steam for the current public build of the dedicated server, or null when the answer has none.</summary>
    Task<string?> GetLatestBuildIdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The dedicated-server executable inside <paramref name="installFolder"/>.</summary>
    string GetServerExePath(string installFolder);
}

public sealed class SteamCmdService : ISteamCmdService
{
    /// <summary>Steam's app id of the Valheim Dedicated Server (the game itself is 892970).</summary>
    public const string DedicatedServerAppId = "896660";

    private const string WindowsDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string LinuxDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    // "Update state (0x61) downloading, progress: 45.12 (1234 / 5678)"
    private static readonly Regex ProgressLine = new(@"progress:\s*(?<pct>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
    private static readonly Regex SuccessLine = new(@"Success! App '" + DedicatedServerAppId + "'", RegexOptions.IgnoreCase);
    private static readonly Regex ErrorLine = new(@"^\s*(ERROR!|FAILED)", RegexOptions.IgnoreCase);
    private static readonly Regex ManifestBuildId = new(@"""buildid""\s+""(?<id>\d+)""", RegexOptions.IgnoreCase);
    private static readonly Regex PublicBranchBuildId = new(@"""public""\s*\{[^{}]*?""buildid""\s+""(?<id>\d+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly IValheimPathResolver _paths;
    private readonly IHttpClientProvider _http;
    private readonly IApplicationLogger _logger;
    private readonly bool _onWindows;

    public SteamCmdService(IValheimPathResolver paths, IHttpClientProvider http, IApplicationLogger logger)
        : this(paths, http, logger, OperatingSystem.IsWindows())
    {
    }

    /// <summary>Test seam: pick the OS layout explicitly.</summary>
    internal SteamCmdService(IValheimPathResolver paths, IHttpClientProvider http, IApplicationLogger logger, bool onWindows)
    {
        _paths = paths;
        _http = http;
        _logger = logger;
        _onWindows = onWindows;
    }

    public string SteamCmdFolder => _paths.GetAppDataPath("steamcmd");

    public string DefaultInstallFolder => _paths.GetAppDataPath("dedicated-server");

    private string LauncherName => _onWindows ? "steamcmd.exe" : "steamcmd.sh";

    public string? FindSteamCmd()
    {
        var path = Path.Join(SteamCmdFolder, LauncherName);
        return File.Exists(path) ? path : null;
    }

    public string GetServerExePath(string installFolder) => Path.Join(installFolder, _paths.ServerBinaryName);

    public async Task<string> DownloadSteamCmdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
    {
        var url = _onWindows ? WindowsDownloadUrl : LinuxDownloadUrl;
        progress?.Report(new SteamCmdProgress(string.Format(Strings.SteamCmd_Log_Downloading, url), null));
        _logger.Information("Downloading SteamCMD from {url}", url);

        Directory.CreateDirectory(SteamCmdFolder);
        using var client = _http.CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Buffered to memory: the archive is a few MB, and the zip reader needs a seekable stream.
        using var buffer = new MemoryStream();
        await download.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        ExtractArchive(buffer, SteamCmdFolder, _onWindows);

        var launcher = FindSteamCmd() ?? throw new FileNotFoundException(string.Format(Strings.SteamCmd_Error_NotInArchive, LauncherName));
        progress?.Report(new SteamCmdProgress(Strings.SteamCmd_Log_Downloaded, null));
        return launcher;
    }

    /// <summary>Unpacks the SteamCMD archive: a zip on Windows, a .tar.gz on Linux (which keeps the executable bits).</summary>
    internal static void ExtractArchive(Stream archive, string destination, bool zip)
    {
        if (zip)
        {
            using var reader = new ZipArchive(archive, ZipArchiveMode.Read);
            reader.ExtractToDirectory(destination, overwriteFiles: true);
            return;
        }

        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    public async Task InstallOrUpdateServerAsync(string installFolder, bool validate, IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installFolder))
            throw new ArgumentException(Strings.SteamCmd_Error_NoFolder);

        var steamCmd = FindSteamCmd() ?? await DownloadSteamCmdAsync(progress, cancellationToken);
        Directory.CreateDirectory(installFolder);

        var args = new List<string> { "+force_install_dir", Path.GetFullPath(installFolder), "+login", "anonymous", "+app_update", DedicatedServerAppId };
        if (validate) args.Add("validate");
        args.Add("+quit");

        _logger.Information("Installing/updating the dedicated server into {folder} (validate: {validate})", installFolder, validate);
        var lines = await RunAsync(steamCmd, args, progress, cancellationToken);

        if (!lines.Exists(l => SuccessLine.IsMatch(l)))
        {
            var error = lines.FindLast(l => ErrorLine.IsMatch(l)) ?? Strings.SteamCmd_Error_NoSuccess;
            throw new InvalidOperationException(string.Format(Strings.SteamCmd_Error_Failed, error.Trim()));
        }
    }

    public string? GetInstalledBuildId(string installFolder)
    {
        var manifest = Path.Join(installFolder, "steamapps", $"appmanifest_{DedicatedServerAppId}.acf");
        if (!File.Exists(manifest)) return null;

        var match = ManifestBuildId.Match(File.ReadAllText(manifest));
        return match.Success ? match.Groups["id"].Value : null;
    }

    public async Task<string?> GetLatestBuildIdAsync(IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
    {
        var steamCmd = FindSteamCmd() ?? await DownloadSteamCmdAsync(progress, cancellationToken);
        var args = new List<string> { "+login", "anonymous", "+app_info_update", "1", "+app_info_print", DedicatedServerAppId, "+quit" };
        var lines = await RunAsync(steamCmd, args, progress, cancellationToken);
        return ParseLatestBuildId(string.Join("\n", lines));
    }

    /// <summary>The public branch's build id in an <c>app_info_print</c> dump.</summary>
    internal static string? ParseLatestBuildId(string appInfo)
    {
        var branches = appInfo.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branches < 0) return null;
        var match = PublicBranchBuildId.Match(appInfo, branches);
        return match.Success ? match.Groups["id"].Value : null;
    }

    internal static double? ParsePercent(string line)
    {
        var match = ProgressLine.Match(line);
        return match.Success && double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
            ? Math.Clamp(pct, 0, 100)
            : null;
    }

    /// <summary>Runs SteamCMD to completion, streaming each output line to <paramref name="progress"/>. Cancelling kills it.</summary>
    private async Task<List<string>> RunAsync(string steamCmd, IReadOnlyList<string> args, IProgress<SteamCmdProgress>? progress, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var start = new ProcessStartInfo(steamCmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (lines) lines.Add(e.Data);
            progress?.Report(new SteamCmdProgress(e.Data, ParsePercent(e.Data)));
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        // SteamCMD's own exit code is unreliable (it returns 0 on some failures and 7 after a successful self-update),
        // so callers judge success from the output.
        _logger.Information("SteamCMD exited with code {code}", process.ExitCode);
        lock (lines) return new List<string>(lines);
    }
}
