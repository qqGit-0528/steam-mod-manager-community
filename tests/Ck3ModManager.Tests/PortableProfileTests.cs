using System.Text;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class PortableProfileTests
{
    [Fact]
    public async Task RoundTripPreservesOrderAndDisabledEntriesWithoutExportingMachineIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        var original = new ModProfile(Guid.NewGuid(), "共享配置", "1.20.0.4",
        [
            new("old-install-b", "Workshop B", "/old/steam/b", 222, "1.20.*") { Enabled = false },
            new("old-install-a", "Workshop A", "/old/steam/a", 111, "1.20.*"),
            new("old-local", "Local addon", "/old/local", null, "1.20.*")
        ], DateTimeOffset.UtcNow);
        using var file = new MemoryStream();
        await PortableProfileService.WriteAsync(file, GameCatalog.Ck3, original, token);
        var json = Encoding.UTF8.GetString(file.ToArray());
        Assert.DoesNotContain("/old/", json);
        Assert.DoesNotContain("old-install", json);
        Assert.DoesNotContain(original.Id.ToString(), json);

        file.Position = 0;
        var imported = await PortableProfileService.ReadAsync(file, GameCatalog.Ck3, token);
        var result = PortableProfileService.Match(imported,
        [
            Installed("new-install-a", "Renamed A", 111),
            Installed("new-local", "local addon", null)
        ]);

        Assert.NotEqual(original.Id, result.Profile.Id);
        Assert.Equal(original.Name, result.Profile.Name);
        Assert.Equal(original.GameVersion, result.Profile.GameVersion);
        Assert.Equal(new ulong?[] { 222, 111, null }, result.Profile.Mods.Select(mod => mod.WorkshopId));
        Assert.Equal(new[] { false, true, true }, result.Profile.Mods.Select(mod => mod.Enabled));
        Assert.Equal(new[] { PortableProfileMatchStatus.Missing, PortableProfileMatchStatus.Installed,
            PortableProfileMatchStatus.Installed }, result.Entries.Select(entry => entry.Status));
        var missing = result.Profile.Mods[0];
        Assert.Empty(missing.ContentPath);
        Assert.True(PortableProfileService.IsUnresolved(missing));

        var available = PortableProfileService.ResolveMissing(missing, [Installed("new-install-b", "Workshop B", 222)]);
        Assert.Equal("new-install-b", available.InstalledModId);
        Assert.False(available.Enabled);
        Assert.False(PortableProfileService.IsUnresolved(available));
    }

    [Fact]
    public void MatchNeverGuessesAmongCopiesOrBetweenWorkshopAndLocalNames()
    {
        var document = new PortableModProfile(1, GameCatalog.Ck3.AppId, "配置", null,
        [
            new("One", 1, true),
            new("Two", null, true),
            new("Three", 3, false)
        ]);
        var available = new[]
        {
            Installed("one-a", "One", 1),
            Installed("one-b", "One", 1),
            Installed("workshop-two", "Two", 2),
            Installed("same-title", "Three", null),
            Installed("unavailable-three", "Three", 3) with { IsAvailable = false }
        };

        var result = PortableProfileService.Match(document, available);

        Assert.Equal(new[] { PortableProfileMatchStatus.Ambiguous, PortableProfileMatchStatus.Missing,
            PortableProfileMatchStatus.Missing }, result.Entries.Select(entry => entry.Status));
        Assert.All(result.Profile.Mods, mod => Assert.Same(mod, PortableProfileService.ResolveMissing(mod, available)));
    }

    [Fact]
    public void ExactDeclaredVersionDisambiguatesButAnUnexpectedDownloadRemainsUnresolved()
    {
        var document = new PortableModProfile(1, GameCatalog.Ck3.AppId, "配置", null, [new("Mod", 7, true, ModVersion: "1.0")]);
        var current = Installed("new", "Mod", 7) with { ModVersion = "2.0" };
        var unknown = Installed("unknown", "Mod", 7) with { ModVersion = null };
        var expected = Installed("old", "Mod", 7) with { ModVersion = "1.0" };

        var mismatch = Assert.Single(PortableProfileService.Match(document, [current, unknown]).Entries);
        Assert.Equal(PortableProfileMatchStatus.VersionMismatch, mismatch.Status);
        Assert.Equal("1.0", mismatch.ExpectedVersion);
        Assert.Equal(new[] { "2.0", null }, mismatch.Candidates.Select(candidate => candidate.ModVersion));
        Assert.True(PortableProfileService.IsUnresolved(mismatch.Mod));
        Assert.Same(mismatch.Mod, PortableProfileService.ResolveMissing(mismatch.Mod, [current]));
        Assert.Same(mismatch.Mod, PortableProfileService.ResolveMissing(mismatch.Mod, [unknown]));

        var match = Assert.Single(PortableProfileService.Match(document, [current, expected]).Entries);
        Assert.Equal(expected.Id, match.Mod.InstalledModId);
        Assert.Equal(expected.Id, PortableProfileService.ResolveMissing(mismatch.Mod, [current, expected]).InstalledModId);

        var alternate = PortableProfileService.Match(document, [current, expected], new Dictionary<int, PortableProfileCandidate>
        {
            [0] = mismatch.Candidates[0]
        });
        Assert.Equal("2.0", Assert.Single(alternate.Profile.Mods).ModVersion);
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"gameAppId":1158310,"name":"x","mods":[]}""")]
    [InlineData("""{"formatVersion":1,"gameAppId":529340,"name":"x","mods":[]}""")]
    [InlineData("""{"formatVersion":1,"gameAppId":1158310,"name":"x","mods":[{"displayName":"a"}]}""")]
    [InlineData("""{"formatVersion":1,"gameAppId":1158310,"name":"x","mods":[{"displayName":"a","enabled":true,"contentPath":"/etc"}]}""")]
    [InlineData("""{"formatVersion":1,"gameAppId":1158310,"name":"x","mods":[{"displayName":"a","enabled":true,"enabled":false}]}""")]
    [InlineData("""{"formatVersion":1,"gameAppId":1158310,"name":"x","mods":[{"displayName":"a","workshopId":8,"enabled":true},{"displayName":"b","workshopId":8,"enabled":false}]}""")]
    public async Task RejectsWrongGameVersionAndInvalidEntries(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableProfileService.ReadAsync(
            stream, GameCatalog.Ck3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopsReadingAtTheFileSizeLimit()
    {
        const int maximumBytes = 1024 * 1024;
        using var stream = new MemoryStream(new byte[maximumBytes * 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableProfileService.ReadAsync(
            stream, GameCatalog.Ck3, TestContext.Current.CancellationToken));
        Assert.Equal(maximumBytes + 1, stream.Position);
    }

    private static InstalledMod Installed(string id, string name, ulong? workshopId) =>
        new(id, name, "/new/" + id, "/new/" + id + ".mod", "1.2", "1.20.*", workshopId, [],
            workshopId is null ? ModOrigin.Local : ModOrigin.SteamWorkshop, true);
}
