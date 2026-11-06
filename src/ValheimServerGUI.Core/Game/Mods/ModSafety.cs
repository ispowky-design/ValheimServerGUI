using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimServerGUI.Game.Mods;

/// <summary>Whether a mod may go on the server.</summary>
public enum ModSafetyVerdict
{
    /// <summary>Runs on the dedicated server alone: players install nothing.</summary>
    ServerOnly,

    /// <summary>BepInExPack, the mod loader itself. Installed on the server only, never needed by players.</summary>
    Loader,

    /// <summary>Players would have to install it too (or the app cannot be sure they would not). Never installed.</summary>
    ClientRequired,
}

/// <summary>Why a mod got its verdict.</summary>
public enum ModSafetyReason
{
    /// <summary>Tagged "Server-side" only, nothing says players need it, and every dependency is server-only too.</summary>
    ServerSideTag,

    /// <summary>The BepInExPack mod loader.</summary>
    Loader,

    /// <summary>Its author tagged it "Client-side": players install it.</summary>
    ClientSideTag,

    /// <summary>Not tagged "Server-side": the app cannot tell, so it refuses.</summary>
    NoServerSideTag,

    /// <summary>Its description says players (clients) need it.</summary>
    DescriptionMentionsClients,

    /// <summary>A modpack: a bundle meant for players' game installs.</summary>
    Modpack,

    /// <summary>Marked deprecated on Thunderstore.</summary>
    Deprecated,

    /// <summary>A dependency is not on Thunderstore (any more).</summary>
    DependencyMissing,

    /// <summary>A dependency is not server-only.</summary>
    DependencyClientRequired,
}

/// <summary>A verdict, its reason, and the detail that goes with it (the dependency, or the words in the description).</summary>
public sealed record ModSafetyResult(ModSafetyVerdict Verdict, ModSafetyReason Reason, string? Detail = null)
{
    public bool IsAllowed => Verdict != ModSafetyVerdict.ClientRequired;
}

/// <summary>
/// The rule that keeps the server vanilla for players: <b>only mods that run on the server alone may be installed</b>.
/// A mod that players have to install too would make them run a modded game (and lose their Steam achievements),
/// so it is refused, and so is every mod the app cannot be sure about. A mod is allowed only when all hold:
/// <list type="number">
/// <item>it is not deprecated and not a modpack;</item>
/// <item>its author tagged it "Server-side" and not "Client-side";</item>
/// <item>its description does not say players or clients need it;</item>
/// <item>every dependency is on Thunderstore and is itself allowed (BepInExPack, the loader, always is).</item>
/// </list>
/// </summary>
public static class ModSafety
{
    /// <summary>The BepInEx pack every Valheim mod loads through.</summary>
    public const string BepInExPackFullName = "denikson-BepInExPack_Valheim";

    public const string ServerSideCategory = "Server-side";
    public const string ClientSideCategory = "Client-side";
    public const string ModpacksCategory = "Modpacks";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Phrases saying players need the mod too. Each match is ignored when negated ("clients don't need it",
    // "no client install required"), see IsNegated. Deliberately broad: a false refusal costs a mod, a false
    // acceptance costs players their achievements.
    private static readonly Regex[] ClientPhrases =
    {
        new(@"\bclients?\s*(?:and|&|\+|/)\s*servers?\b", Options),
        new(@"\bservers?\s*(?:and|&|\+|/)\s*(?:the\s+|all\s+)?clients?\b", Options),
        new(@"\bboth\s+(?:the\s+)?(?:clients?|servers?|sides)\b", Options),
        new(@"\bclients?[- ]side\b", Options),
        new(@"\bclients?\s+(?:must|needs?|should|have\s+to|has\s+to|also|requires?)\b", Options),
        new(@"\b(?:all|every|each)\s+(?:the\s+)?(?:clients?|players?|users?)\s+(?:must|needs?|should|have\s+to|has\s+to|requires?)\b", Options),
        new(@"\b(?:install(?:ed)?|required|needed|necessary)\s+(?:on|by|for|in)\s+(?:all\s+|every\s+|each\s+|the\s+)*(?:clients?|players?)\b", Options),
        new(@"\brequires?\s+(?:the\s+)?(?:mod\s+)?(?:on\s+)?(?:the\s+)?clients?\b", Options),
    };

    private static readonly Regex Negation = new(@"\b(?:not|no|never|without|dont|doesnt|nor)\b|n't\b|n’t\b", Options);

    /// <summary>
    /// The verdict for <paramref name="package"/>. <paramref name="find"/> looks a dependency up by its
    /// <c>Owner-Name</c> id (null when it is not on Thunderstore).
    /// </summary>
    public static ModSafetyResult Classify(ThunderstorePackage package, Func<string, ThunderstorePackage?> find)
        => Classify(package, find, new Dictionary<string, ModSafetyResult>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static ModSafetyResult Classify(
        ThunderstorePackage package,
        Func<string, ThunderstorePackage?> find,
        Dictionary<string, ModSafetyResult> known,
        HashSet<string> visiting)
    {
        if (known.TryGetValue(package.FullName, out var cached)) return cached;
        visiting.Add(package.FullName);
        var result = ClassifyOwn(package) ?? ClassifyDependencies(package, find, known, visiting);
        visiting.Remove(package.FullName);
        known[package.FullName] = result;
        return result;
    }

    /// <summary>The verdict from the package alone, or null when only its dependencies are left to check.</summary>
    private static ModSafetyResult? ClassifyOwn(ThunderstorePackage package)
    {
        if (IsLoader(package.FullName)) return new ModSafetyResult(ModSafetyVerdict.Loader, ModSafetyReason.Loader);
        if (package.IsDeprecated) return Refused(ModSafetyReason.Deprecated);
        if (package.HasCategory(ModpacksCategory)) return Refused(ModSafetyReason.Modpack);
        if (package.HasCategory(ClientSideCategory)) return Refused(ModSafetyReason.ClientSideTag);
        if (!package.HasCategory(ServerSideCategory)) return Refused(ModSafetyReason.NoServerSideTag);
        if (FindClientPhrase(package.Latest.Description) is { } phrase) return Refused(ModSafetyReason.DescriptionMentionsClients, phrase);
        return null;
    }

    private static ModSafetyResult ClassifyDependencies(
        ThunderstorePackage package,
        Func<string, ThunderstorePackage?> find,
        Dictionary<string, ModSafetyResult> known,
        HashSet<string> visiting)
    {
        foreach (var text in package.Latest.Dependencies)
        {
            var dependency = ThunderstoreDependency.Parse(text);
            if (dependency is null) return Refused(ModSafetyReason.DependencyMissing, text);
            var name = dependency.Value.FullName;
            if (IsLoader(name) || visiting.Contains(name)) continue;

            var found = find(name);
            if (found is null) return Refused(ModSafetyReason.DependencyMissing, name);
            if (!Classify(found, find, known, visiting).IsAllowed) return Refused(ModSafetyReason.DependencyClientRequired, found.FullName);
        }
        return new ModSafetyResult(ModSafetyVerdict.ServerOnly, ModSafetyReason.ServerSideTag);
    }

    public static bool IsLoader(string fullName) => string.Equals(fullName, BepInExPackFullName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first phrase in <paramref name="description"/> that says players need the mod, or null.</summary>
    public static string? FindClientPhrase(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        foreach (var pattern in ClientPhrases)
        {
            foreach (Match match in pattern.Matches(description))
            {
                if (!IsNegated(description, match)) return match.Value;
            }
        }
        return null;
    }

    // "no client-side install", "clients do not need", "doesn't need to be installed on clients": a negation word
    // just before the match, or inside it.
    private static bool IsNegated(string text, Match match)
    {
        var start = Math.Max(0, match.Index - 24);
        var window = text[start..(match.Index + match.Length)];
        // Only the words of the same sentence count.
        var sentenceStart = window.LastIndexOfAny(new[] { '.', '!', '?', ';', '\n' }, Math.Max(0, match.Index - start - 1));
        if (sentenceStart >= 0) window = window[(sentenceStart + 1)..];
        return Negation.IsMatch(window);
    }

    private static ModSafetyResult Refused(ModSafetyReason reason, string? detail = null)
        => new(ModSafetyVerdict.ClientRequired, reason, detail);
}
