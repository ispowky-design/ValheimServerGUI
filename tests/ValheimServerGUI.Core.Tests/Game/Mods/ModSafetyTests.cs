using System.Linq;
using ValheimServerGUI.Game.Mods;
using Xunit;
using static ValheimServerGUI.Core.Tests.Game.Mods.ModTestPackages;

namespace ValheimServerGUI.Core.Tests.Game.Mods;

/// <summary>
/// The server-only rule: a mod is allowed only when tagged "Server-side" (and not "Client-side"), nothing in its
/// description says players need it, and all its dependencies are allowed too. Anything unsure is refused.
/// </summary>
public class ModSafetyTests
{
    private static ModSafetyResult Classify(ThunderstorePackage package, params ThunderstorePackage[] others)
        => Catalog(others.Prepend(package)).Classify(package);

    private static ThunderstoreCatalogSnapshot Catalog(System.Collections.Generic.IEnumerable<ThunderstorePackage> packages)
        => ModTestPackages.Catalog(System.Linq.Enumerable.ToArray(packages));

    [Fact]
    public void ServerSideTagOnly_IsAllowed()
    {
        var result = Classify(Package("Owner-ServerMod", categories: new[] { "Server-side", "Tweaks" }));
        Assert.Equal(ModSafetyVerdict.ServerOnly, result.Verdict);
        Assert.Equal(ModSafetyReason.ServerSideTag, result.Reason);
        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("Server Side")]
    [InlineData("server-side")]
    [InlineData("Server_side")]
    public void ServerSideTag_SpellingVariants_Count(string tag)
        => Assert.True(Classify(Package("Owner-Mod", categories: new[] { tag })).IsAllowed);

    [Fact]
    public void ClientAndServerTags_AreRefused()
    {
        var result = Classify(Package("Owner-Both", categories: new[] { "Server-side", "Client-side" }));
        Assert.False(result.IsAllowed);
        Assert.Equal(ModSafetyReason.ClientSideTag, result.Reason);
    }

    [Fact]
    public void NoServerSideTag_IsRefused_BecauseUnsure()
    {
        var result = Classify(Package("Owner-Untagged", categories: new[] { "Mods", "Tweaks" }));
        Assert.False(result.IsAllowed);
        Assert.Equal(ModSafetyReason.NoServerSideTag, result.Reason);
    }

    [Fact]
    public void Modpack_IsRefused()
        => Assert.Equal(ModSafetyReason.Modpack, Classify(Package("Owner-Pack", categories: new[] { "Server-side", "Modpacks" })).Reason);

    [Fact]
    public void Deprecated_IsRefused()
        => Assert.Equal(ModSafetyReason.Deprecated, Classify(Package("Owner-Old", deprecated: true)).Reason);

    [Theory]
    [InlineData("Must be installed on both client and server.")]
    [InlineData("Install on server and all clients.")]
    [InlineData("Syncs the config between server and clients.")]
    [InlineData("Requires the mod on the client.")]
    [InlineData("All players must install this mod.")]
    [InlineData("Clients need this too for the UI.")]
    [InlineData("This is required on every client.")]
    [InlineData("Works client-side and server-side.")]
    public void DescriptionSayingPlayersNeedIt_IsRefused(string description)
    {
        var result = Classify(Package("Owner-Mod", description: description));
        Assert.False(result.IsAllowed);
        Assert.Equal(ModSafetyReason.DescriptionMentionsClients, result.Reason);
        Assert.NotNull(result.Detail);
    }

    [Theory]
    [InlineData("Server only. Clients don't need to install anything.")]
    [InlineData("Runs on the dedicated server; no client install required.")]
    [InlineData("Vanilla players can join. Not required on clients.")]
    [InlineData("Backs up the world every hour.")]
    [InlineData("Does not need to be installed on clients.")]
    public void DescriptionThatOnlyMentionsClientsNegatively_IsAllowed(string description)
        => Assert.True(Classify(Package("Owner-Mod", description: description)).IsAllowed);

    [Fact]
    public void BepInExPack_IsTheLoader()
    {
        var result = Classify(BepInExPack());
        Assert.Equal(ModSafetyVerdict.Loader, result.Verdict);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void DependingOnlyOnBepInExPack_IsAllowed()
        => Assert.True(Classify(Package("Owner-Mod", dependencies: new[] { "denikson-BepInExPack_Valheim-5.4.2202" }), BepInExPack()).IsAllowed);

    [Fact]
    public void ClientDependency_RefusesTheMod()
    {
        var library = Package("Lib-ClientLib", categories: new[] { "Libraries", "Client-side", "Server-side" });
        var result = Classify(Package("Owner-Mod", dependencies: new[] { "Lib-ClientLib-2.0.0" }), library);
        Assert.False(result.IsAllowed);
        Assert.Equal(ModSafetyReason.DependencyClientRequired, result.Reason);
        Assert.Equal("Lib-ClientLib", result.Detail);
    }

    [Fact]
    public void TransitiveClientDependency_RefusesTheMod()
    {
        var deep = Package("Lib-Deep", categories: new[] { "Libraries" });
        var middle = Package("Lib-Middle", dependencies: new[] { "Lib-Deep-1.0.0" });
        var result = Classify(Package("Owner-Mod", dependencies: new[] { "Lib-Middle-1.0.0" }), middle, deep);
        Assert.False(result.IsAllowed);
        Assert.Equal("Lib-Middle", result.Detail);
    }

    [Fact]
    public void MissingDependency_RefusesTheMod()
    {
        var result = Classify(Package("Owner-Mod", dependencies: new[] { "Gone-Library-1.0.0" }));
        Assert.Equal(ModSafetyReason.DependencyMissing, result.Reason);
        Assert.Equal("Gone-Library", result.Detail);
    }

    [Fact]
    public void ServerOnlyDependency_IsAllowed()
    {
        var library = Package("Lib-ServerLib", categories: new[] { "Libraries", "Server-side" });
        Assert.True(Classify(Package("Owner-Mod", dependencies: new[] { "Lib-ServerLib-1.0.0" }), library).IsAllowed);
    }

    [Fact]
    public void DependencyCycle_DoesNotLoop()
    {
        var a = Package("Owner-A", dependencies: new[] { "Owner-B-1.0.0" });
        var b = Package("Owner-B", dependencies: new[] { "Owner-A-1.0.0" });
        Assert.True(Classify(a, b).IsAllowed);
    }

    [Theory]
    [InlineData("denikson-BepInExPack_Valheim-5.4.2202", "denikson-BepInExPack_Valheim", "5.4.2202")]
    [InlineData("Some-Team-ModName-1.2.3", "Some-Team-ModName", "1.2.3")]
    public void DependencyStrings_Parse(string text, string fullName, string version)
    {
        var parsed = ThunderstoreDependency.Parse(text);
        Assert.Equal(new ThunderstoreDependency(fullName, version), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoDashes")]
    [InlineData("Name-")]
    [InlineData("OnlyName-1.0.0")]
    public void BadDependencyStrings_DoNotParse(string text) => Assert.Null(ThunderstoreDependency.Parse(text));
}
