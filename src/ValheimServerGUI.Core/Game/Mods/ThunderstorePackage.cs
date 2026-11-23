using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValheimServerGUI.Game.Mods;

/// <summary>
/// One Valheim package on Thunderstore, reduced to what the app needs: its identity, the categories its author
/// tagged it with (the "Server-side" / "Client-side" tags drive <see cref="ModSafety"/>), and its latest version.
/// </summary>
public sealed record ThunderstorePackage(
    string Owner,
    string Name,
    IReadOnlyList<string> Categories,
    bool IsDeprecated,
    bool HasNsfwContent,
    string? PackageUrl,
    DateTime DateUpdated,
    long TotalDownloads,
    int RatingScore,
    ThunderstoreVersion Latest)
{
    /// <summary>The version-less id Thunderstore uses in dependency strings: <c>Owner-Name</c>.</summary>
    [JsonIgnore]
    public string FullName => Owner + "-" + Name;

    /// <summary>The name as Thunderstore shows it (underscores read as spaces).</summary>
    [JsonIgnore]
    public string DisplayName => Name.Replace('_', ' ');

    public bool HasCategory(string category)
    {
        foreach (var c in Categories)
        {
            if (string.Equals(Normalize(c), Normalize(category), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // "Server-side", "Server Side" and "server_side" are the same tag.
    private static string Normalize(string category) => category.Replace(" ", "").Replace("-", "").Replace("_", "");
}

/// <summary>A package's version: what it says about itself, what it needs, and where its zip is.</summary>
public sealed record ThunderstoreVersion(
    string VersionNumber,
    string Description,
    IReadOnlyList<string> Dependencies,
    string DownloadUrl,
    long FileSize,
    string? IconUrl,
    string? WebsiteUrl);

/// <summary>A dependency string (<c>Owner-Name-1.2.3</c>) split into its package id and version.</summary>
public readonly record struct ThunderstoreDependency(string FullName, string Version)
{
    /// <summary>Parses <c>Owner-Name-1.2.3</c>; the version follows the last dash, the name the one before.</summary>
    public static ThunderstoreDependency? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var lastDash = trimmed.LastIndexOf('-');
        if (lastDash <= 0 || lastDash == trimmed.Length - 1) return null;
        var fullName = trimmed[..lastDash];
        if (fullName.IndexOf('-') <= 0) return null;
        return new ThunderstoreDependency(fullName, trimmed[(lastDash + 1)..]);
    }
}
