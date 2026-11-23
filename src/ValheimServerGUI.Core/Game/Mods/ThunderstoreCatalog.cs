using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game.Mods;

/// <summary>The Valheim package list at one point in time.</summary>
public sealed class ThunderstoreCatalogSnapshot
{
    private readonly Dictionary<string, ThunderstorePackage> _byName;

    public ThunderstoreCatalogSnapshot(IReadOnlyList<ThunderstorePackage> packages, DateTime fetchedAtUtc, bool isStale)
    {
        Packages = packages;
        FetchedAtUtc = fetchedAtUtc;
        IsStale = isStale;
        _byName = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages) _byName.TryAdd(package.FullName, package);
    }

    public IReadOnlyList<ThunderstorePackage> Packages { get; }

    public DateTime FetchedAtUtc { get; }

    /// <summary>Thunderstore could not be reached; this is the last list saved on disk.</summary>
    public bool IsStale { get; }

    /// <summary>The package with the <c>Owner-Name</c> id, or null.</summary>
    public ThunderstorePackage? Find(string fullName) => _byName.GetValueOrDefault(fullName);

    public ModSafetyResult Classify(ThunderstorePackage package) => ModSafety.Classify(package, Find);
}

/// <summary>Reads Thunderstore's Valheim package list and downloads package zips.</summary>
public interface IThunderstoreCatalog
{
    /// <summary>
    /// The package list: the copy saved on disk while it is recent, else a fresh one from Thunderstore (saved for next
    /// time). <paramref name="forceRefresh"/> always asks Thunderstore. When Thunderstore cannot be reached, the saved
    /// copy is returned (<see cref="ThunderstoreCatalogSnapshot.IsStale"/>); with none, the error is thrown.
    /// </summary>
    Task<ThunderstoreCatalogSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>Downloads the zip of <paramref name="package"/>'s latest version.</summary>
    Task<byte[]> DownloadAsync(ThunderstorePackage package, CancellationToken cancellationToken);
}

public sealed class ThunderstoreCatalog : IThunderstoreCatalog
{
    public const string PackageListUrl = "https://thunderstore.io/c/valheim/api/v1/package/";

    /// <summary>How long a saved list is used before asking Thunderstore again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    /// <summary>Largest zip the app downloads; mods are a few MB at most.</summary>
    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    private readonly IHttpClientProvider _http;
    private readonly IApplicationLogger _logger;
    private readonly string _cachePath;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ThunderstoreCatalogSnapshot? _memory;

    public ThunderstoreCatalog(IValheimPathResolver paths, IHttpClientProvider http, IApplicationLogger logger)
        : this(paths.GetAppDataPath("thunderstore-valheim.json"), http, logger, () => DateTime.UtcNow)
    {
    }

    internal ThunderstoreCatalog(string cachePath, IHttpClientProvider http, IApplicationLogger logger, Func<DateTime> utcNow)
    {
        _cachePath = cachePath;
        _http = http;
        _logger = logger;
        _utcNow = utcNow;
    }

    public async Task<ThunderstoreCatalogSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh)
            {
                _memory ??= await Task.Run(ReadCache, cancellationToken);
                if (_memory is not null && _utcNow() - _memory.FetchedAtUtc < CacheLifetime) return _memory;
            }

            try
            {
                var packages = await FetchAsync(cancellationToken);
                var snapshot = new ThunderstoreCatalogSnapshot(packages, _utcNow(), isStale: false);
                _logger.Information("Read {count} Valheim packages from Thunderstore", packages.Count);
                await Task.Run(() => WriteCache(snapshot), cancellationToken);
                _memory = snapshot;
                return snapshot;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.Warning("Could not read the Thunderstore package list: {message}", e.Message);
                var saved = _memory ?? await Task.Run(ReadCache, cancellationToken);
                if (saved is null) throw;
                _memory = saved;
                return new ThunderstoreCatalogSnapshot(saved.Packages, saved.FetchedAtUtc, isStale: true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<byte[]> DownloadAsync(ThunderstorePackage package, CancellationToken cancellationToken)
    {
        var url = package.Latest.DownloadUrl;
        if (!IsThunderstoreUrl(url)) throw new InvalidDataException($"Refusing to download {package.FullName} from outside Thunderstore: {url}");

        _logger.Information("Downloading {package} {version} from {url}", package.FullName, package.Latest.VersionNumber, url);
        using var client = _http.CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException($"{package.FullName} is too large to download.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDownloadBytes) throw new InvalidDataException($"{package.FullName} is too large to download.");
        }
        return buffer.ToArray();
    }

    /// <summary>Only Thunderstore's own hosts (thunderstore.io and its CDN subdomains) over HTTPS.</summary>
    public static bool IsThunderstoreUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && (uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<ThunderstorePackage>> FetchAsync(CancellationToken cancellationToken)
    {
        using var client = _http.CreateClient();
        // The list is large (every Valheim package with all its versions): read it as a stream, one package at a time.
        client.Timeout = TimeSpan.FromMinutes(5);
        using var request = new HttpRequestMessage(HttpMethod.Get, PackageListUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await Task.Run(() => ParsePackageList(stream), cancellationToken);
    }

    /// <summary>Parses Thunderstore's v1 package list (a JSON array of packages, each with its versions, newest first).</summary>
    internal static IReadOnlyList<ThunderstorePackage> ParsePackageList(Stream stream)
    {
        using var text = new StreamReader(stream);
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        var packages = new List<ThunderstorePackage>();
        if (!reader.Read() || reader.TokenType != JsonToken.StartArray) throw new JsonReaderException("The Thunderstore package list is not a JSON array.");

        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        {
            if (reader.TokenType != JsonToken.StartObject) continue;
            var item = JObject.Load(reader);
            if (ParsePackage(item) is { } package) packages.Add(package);
        }
        return packages;
    }

    private static ThunderstorePackage? ParsePackage(JObject item)
    {
        var owner = (string?)item["owner"];
        var name = (string?)item["name"];
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name)) return null;

        var versions = item["versions"] as JArray;
        var latest = versions?.OfType<JObject>().FirstOrDefault(v => (bool?)v["is_active"] ?? true);
        if (latest is null) return null;
        var downloadUrl = (string?)latest["download_url"];
        var versionNumber = (string?)latest["version_number"];
        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(versionNumber)) return null;

        long downloads = 0;
        foreach (var v in versions!.OfType<JObject>()) downloads += (long?)v["downloads"] ?? 0;

        return new ThunderstorePackage(
            Owner: owner,
            Name: name,
            Categories: (item["categories"] as JArray)?.Values<string>().Where(c => c is not null).Select(c => c!).ToArray() ?? Array.Empty<string>(),
            IsDeprecated: (bool?)item["is_deprecated"] ?? false,
            HasNsfwContent: (bool?)item["has_nsfw_content"] ?? false,
            PackageUrl: (string?)item["package_url"],
            DateUpdated: DateTime.TryParse((string?)item["date_updated"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var updated) ? updated : DateTime.MinValue,
            TotalDownloads: downloads,
            RatingScore: (int?)item["rating_score"] ?? 0,
            Latest: new ThunderstoreVersion(
                VersionNumber: versionNumber,
                Description: (string?)latest["description"] ?? "",
                Dependencies: (latest["dependencies"] as JArray)?.Values<string>().Where(d => d is not null).Select(d => d!).ToArray() ?? Array.Empty<string>(),
                DownloadUrl: downloadUrl,
                FileSize: (long?)latest["file_size"] ?? 0,
                IconUrl: (string?)latest["icon"],
                WebsiteUrl: (string?)latest["website_url"]));
    }

    // ----- disk cache: the reduced list, so the next start does not download everything again -----

    private sealed class CacheFile
    {
        [JsonProperty("fetchedAtUtc")] public DateTime FetchedAtUtc { get; set; }
        [JsonProperty("packages")] public List<ThunderstorePackage> Packages { get; set; } = new();
    }

    private ThunderstoreCatalogSnapshot? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_cachePath));
            if (file?.Packages is null) return null;
            var packages = file.Packages.Where(p => p?.Latest is not null && p.Categories is not null).ToList();
            return new ThunderstoreCatalogSnapshot(packages, DateTime.SpecifyKind(file.FetchedAtUtc, DateTimeKind.Utc), isStale: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning("Ignoring the saved Thunderstore package list {path}: {message}", _cachePath, e.Message);
            return null;
        }
    }

    private void WriteCache(ThunderstoreCatalogSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var file = new CacheFile { FetchedAtUtc = snapshot.FetchedAtUtc, Packages = snapshot.Packages.ToList() };
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(file));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Could not save the Thunderstore package list to {path}: {message}", _cachePath, e.Message);
        }
    }
}
