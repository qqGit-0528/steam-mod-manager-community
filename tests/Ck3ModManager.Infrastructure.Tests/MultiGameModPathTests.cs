using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class MultiGameModPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("game-mod-paths-").FullName;

    [Theory]
    [InlineData("ck3")]
    [InlineData("hoi4")]
    [InlineData("eu4")]
    [InlineData("stellaris")]
    [InlineData("victoria3")]
    [InlineData("imperator")]
    [InlineData("prisonarchitect")]
    public async Task BuiltInGamesScanTheirOwnLocalAndWorkshopFormats(string id)
    {
        var game = GameCatalog.Get(id);
        var user = Path.Combine(_root, "user");
        var steam = Path.Combine(_root, "steam");
        var local = Path.Combine(user, game.LocalModDirectoryName, "Pack");
        var workshop = Path.Combine(steam, "steamapps", "workshop", "content", game.AppId.ToString(), "123");
        WriteMod(game, local, "Local pack");
        WriteMod(game, workshop, "Workshop pack");
        if (id is not "prisonarchitect" and not "victoria3")
            Write(local + ".mod", "name=\"Local pack\" path=\"mod/Pack\"");

        var environment = await new SteamGameLocator(game, [steam], _root)
            .DiscoverAsync(new(UserDataDirectory: user), TestContext.Current.CancellationToken);
        var result = await Scanner(game).ScanAsync(environment, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Mods.Count);
        Assert.Contains(result.Mods, mod => mod.Name == "Local pack" && mod.ContentPath == local);
        Assert.Contains(result.Mods, mod => mod.Name == "Workshop pack" && mod.ContentPath == workshop && mod.WorkshopId == 123);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("ck3")]
    [InlineData("prisonarchitect")]
    public async Task LinuxFindsProtonUserDataAndAnExplicitOverrideRemainsAuthoritative(string id)
    {
        var game = GameCatalog.Get(id);
        var steam = Path.Combine(_root, "steam");
        var prefix = Path.Combine(steam, "steamapps", "compatdata", game.AppId.ToString(), "pfx", "drive_c", "users", "steamuser");
        var protonUser = id == "prisonarchitect"
            ? Path.Combine(prefix, "AppData", "Local", "Introversion", "Prison Architect")
            : Path.Combine(prefix, "Documents", "Paradox Interactive", game.GameName);
        var content = Path.Combine(protonUser, game.LocalModDirectoryName, "Pack");
        WriteMod(game, content, "Proton pack");
        if (id != "prisonarchitect") Write(content + ".mod", "name=\"Proton pack\" path=\"mod/Pack\"");
        var locator = new SteamGameLocator(game, [steam], _root);
        var environment = await locator.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        var result = await Scanner(game).ScanAsync(environment, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(protonUser, environment.UserDataDirectory);
            Assert.Contains(Path.GetDirectoryName(content)!, environment.LocalModWatchPaths);
            Assert.Equal(content, Assert.Single(result.Mods).ContentPath);
        }
        else Assert.Empty(result.Mods);

        var manual = Path.Combine(_root, "manual");
        Directory.CreateDirectory(manual);
        var overridden = await locator.DiscoverAsync(new(UserDataDirectory: manual), TestContext.Current.CancellationToken);
        Assert.Equal(manual, overridden.UserDataDirectory);
        Assert.Empty((await Scanner(game).ScanAsync(overridden, TestContext.Current.CancellationToken)).Mods);
    }

    [Fact]
    public async Task EachParadoxDescriptorResolvesRelativePathsFromItsOwnUserDataDirectory()
    {
        var first = Path.Combine(_root, "native");
        var second = Path.Combine(_root, "proton");
        foreach (var user in new[] { first, second })
        {
            Write(Path.Combine(user, "mod", "Pack", "content.txt"), "mod");
            Write(Path.Combine(user, "mod", "Pack.mod"), "name=\"Pack\" path=\"mod/Pack\"");
        }
        var environment = new GameEnvironment(null, first, null, [], [])
        {
            LocalModDirectories = [Path.Combine(first, "mod"), Path.Combine(second, "mod")]
        };
        var result = await new Ck3ModScanner().ScanAsync(environment, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { Path.Combine(first, "mod", "Pack"), Path.Combine(second, "mod", "Pack") }.Order(),
            result.Mods.Select(mod => mod.ContentPath).Order());
        Assert.Empty(result.Warnings);
    }

    private static IModScanner Scanner(GameDefinition game) => game.Id == "prisonarchitect"
        ? new PrisonArchitectModScanner() : new Ck3ModScanner();

    private static void WriteMod(GameDefinition game, string folder, string name)
    {
        if (game.Id == "prisonarchitect") Write(Path.Combine(folder, "manifest.txt"), $"Name \"{name}\" Version \"v1\" FileID \"-1\"");
        else if (game.Id == "victoria3") Write(Path.Combine(folder, ".metadata", "metadata.json"), $"{{\"name\":\"{name}\",\"supported_game_version\":\"1.*\"}}");
        else Write(Path.Combine(folder, "descriptor.mod"), $"name=\"{name}\"");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
