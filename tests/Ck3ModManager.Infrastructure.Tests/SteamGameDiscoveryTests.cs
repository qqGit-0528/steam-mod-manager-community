using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamGameDiscoveryTests
{
    [Fact]
    public async Task DiscoversInstalledGamesAcrossLibrariesAndResolvesTheirSeparateModDirectories()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        fixture.File("steam/steamapps/libraryfolders.vdf",
            $$""" "libraryfolders" { "1" { "path" {{JsonSerializer.Serialize(fixture.Secondary)}} } } """);
        fixture.Install(801001, "New Game", "secondary", flags: "6");
        fixture.Install(801002, "Local Game");
        fixture.Install(801003, "No Mods");
        fixture.Install(801004, "Incomplete Game", flags: "2");
        fixture.Install(801005, "Broken Game", manifestId: 801006);
        fixture.Install(801001, "Old Partial Copy", flags: "2");
        var workshopFile = fixture.File("steam/steamapps/workshop/content/801001/145/assets.rim", "workshop data");
        fixture.File("secondary/steamapps/workshop/content/801001/146.bin", "legacy item");
        fixture.File("secondary/steamapps/workshop/content/8010022/150/wrong.rim", "unrelated game");
        fixture.File("secondary/steamapps/workshop/content/801004/148/fake.txt", "incomplete install");
        var installedMod = fixture.File("secondary/steamapps/common/New Game/Mods/LocalMod/info.xml", "local mod");
        var userMod = fixture.File("home/Documents/My Games/New Game/mods/Add-on.pak", "package");
        fixture.File("home/Documents/Local Game/Mods/Offline Mod/data.txt", "standalone mod");
        fixture.File("home/Documents/My Games/New Game/mods/.metadata/cache", "hidden folder");
        fixture.File("home/Documents/My Games/New Game/mods/readme.txt", "not a mod package");
        using var http = new HttpClient(new StoreHandler(_ => throw new HttpRequestException("Offline")));
        var discovery = new SteamGameDiscovery(http, [], fixture.Home);

        var catalog = await discovery.DiscoverAsync(fixture.Steam, token);

        Assert.Equal(GameCatalog.All.Count + 1, catalog.Count);
        Assert.Equal(GameCatalog.All, catalog.Take(GameCatalog.All.Count));
        var game = Assert.Single(catalog, item => item.Id == "steam-801001");
        Assert.True(game.HasSteamWorkshop);
        Assert.False(game.SupportsNativeWorkshopDownloads);
        Assert.DoesNotContain(catalog, item => item.Id == "steam-801002");

        var locator = new SteamGameLocator(game, [], fixture.Home);
        var environment = await locator.DiscoverAsync(new(), token);
        Assert.Equal(Path.Combine(fixture.Secondary, "steamapps", "common", "New Game"), environment.GameDirectory);
        Assert.Equal(2, environment.WorkshopDirectories.Count);
        Assert.Equal(2, environment.LocalModDirectories.Count);

        var result = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(environment, token);
        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.Mods.Count);
        Assert.Contains(result.Mods, mod => mod.WorkshopId == 145 && mod.ContentPath == Path.GetDirectoryName(workshopFile));
        Assert.Contains(result.Mods, mod => mod.WorkshopId == 146 && mod.ContentPath.EndsWith("146.bin", StringComparison.Ordinal));
        Assert.Contains(result.Mods, mod => mod.Name == "LocalMod" && mod.ContentPath == Path.GetDirectoryName(installedMod));
        Assert.Contains(result.Mods, mod => mod.Name == "Add-on.pak" && mod.ContentPath == userMod);

        var custom = Path.Combine(fixture.Root, "custom-folder");
        fixture.File("custom-folder/other.tmod", "custom mod");
        var manual = await locator.DiscoverAsync(new(UserDataDirectory: custom), token);
        Assert.Equal(custom, Assert.Single(manual.LocalModDirectories));
        Assert.Equal(environment.WorkshopDirectories, manual.WorkshopDirectories);
        var customScan = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(manual, token);
        Assert.Equal("other.tmod", Assert.Single(customScan.Mods, mod => mod.Origin == ModOrigin.Local).Name);
    }

    [Fact]
    public async Task RecognizesWorkshopBeforeSubscriptionAndDiscoversLaterInstalls()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        fixture.Install(802001, "Workshop Game");
        fixture.Install(802002, "Non-Workshop Game");
        fixture.Install(802003, "Workshop Tool");
        fixture.Install(802004, "Manifest Only");
        fixture.File("steam/steamapps/workshop/appworkshop_802004.acf", """ "AppWorkshop" { "appid" "802004" } """);
        var handler = new StoreHandler(id => id switch
        {
            802001 or 802005 => ("game", 30),
            802003 => ("tool", 30),
            _ => ("game", 2)
        });
        using var http = new HttpClient(handler);
        var discovery = new SteamGameDiscovery(http, [fixture.Steam], fixture.Home);

        var catalog = await discovery.DiscoverAsync(cancellationToken: token);

        Assert.Equal(new uint[] { 802004, 802001 }, catalog.Where(game => game.IsDiscovered).Select(game => game.AppId));
        Assert.DoesNotContain(802004u, handler.Requests);
        var game = Assert.Single(catalog, game => game.AppId == 802001);
        var locator = new SteamGameLocator(game, [fixture.Steam], fixture.Home);
        var environment = await locator.DiscoverAsync(new(), token);
        Assert.Empty(environment.WorkshopDirectories);
        var expected = SteamGameLocator.WorkshopPath(fixture.Steam, game.AppId);
        Assert.Contains(expected, environment.LocalModWatchPaths);

        fixture.Install(802005, "Later Game");
        fixture.File("steam/steamapps/workshop/content/802001/71/mod.xml", "first subscription");
        var updated = await discovery.DiscoverAsync(cancellationToken: token);
        Assert.Contains(updated, item => item.AppId == 802005);
        Assert.Equal(expected, Assert.Single((await locator.DiscoverAsync(new(), token)).WorkshopDirectories));
        Assert.Single(handler.Requests, appId => appId == 802001);
    }

    [Fact]
    public async Task DiscoversUnityModPathsFromTheInstalledAppAndPublisherMetadata()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        fixture.Install(803001, "Unity Game");
        fixture.File("steam/steamapps/workshop/appworkshop_803001.acf", """ "AppWorkshop" { "appid" "803001" } """);
        const string install = "steam/steamapps/common/Unity Game";
        fixture.File($"{install}/UnityGame.app/Contents/Resources/Data/app.info", "Game Studio\nGame Product");
        fixture.File($"{install}/UnityGame_Data/app.info", "Game Studio\nGame Product");
        var bundle = fixture.File($"{install}/UnityGame.app/Contents/Mods/Bundle Mod/info.ini", "bundle mod");
        var data = fixture.File($"{install}/UnityGame_Data/Mods/Data Mod/info.ini", "data mod");
        var userDirectory = OperatingSystem.IsMacOS() ? "home/Library/Application Support/Game Studio/Game Product"
            : OperatingSystem.IsWindows() ? "home/AppData/LocalLow/Game Studio/Game Product"
            : "home/.config/unity3d/Game Studio/Game Product";
        var user = fixture.File($"{userDirectory}/Mods/User Mod/info.ini", "user mod");
        using var http = new HttpClient(new StoreHandler(_ => throw new HttpRequestException("Offline")));
        var discovery = new SteamGameDiscovery(http, [fixture.Steam], fixture.Home);
        var catalog = await discovery.DiscoverAsync(cancellationToken: token);
        var game = Assert.Single(catalog, item => item.AppId == 803001);

        var locator = new SteamGameLocator(game, [fixture.Steam], fixture.Home);
        var environment = await locator.DiscoverAsync(new(), token);
        var result = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(environment, token);
        Assert.Equal(3, environment.LocalModDirectories.Count);
        Assert.Equal(new[] { bundle, data, user }.Select(Path.GetDirectoryName).Order(),
            result.Mods.Select(mod => mod.ContentPath).Order());
    }

    [Fact]
    public async Task FindsEngineModDirectoriesWithoutRequiringReadableUnityIdentity()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        fixture.Install(803002, "No Identity Game");
        fixture.File("steam/steamapps/workshop/appworkshop_803002.acf", """ "AppWorkshop" { "appid" "803002" } """);
        var pack = fixture.File("steam/steamapps/common/No Identity Game/Game_Data/Mods/Pack/data.txt", "mod");
        fixture.File("steam/steamapps/common/No Identity Game/Game_Data/app.info", "invalid identity");
        using var http = new HttpClient(new StoreHandler(_ => throw new HttpRequestException("Offline")));
        var game = Assert.Single((await new SteamGameDiscovery(http, [fixture.Steam], fixture.Home)
            .DiscoverAsync(cancellationToken: token)).Where(game => game.IsDiscovered));
        var environment = await new SteamGameLocator(game, [fixture.Steam], fixture.Home).DiscoverAsync(new(), token);
        var result = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(environment, token);
        Assert.Equal(Path.GetDirectoryName(pack), Assert.Single(result.Mods).ContentPath);
    }

    [Fact]
    public async Task RestartDiscoveryReadsSteamRootsSavedForOtherGames()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        var storage = Path.Combine(fixture.Root, "storage");
        fixture.Install(803003, "External Game", "secondary");
        fixture.File("secondary/steamapps/workshop/appworkshop_803003.acf", """ "AppWorkshop" { "appid" "803003" } """);
        fixture.File("secondary/steamapps/common/External Game/Mods/Pack/data.txt", "mod");
        await new JsonSettingsStore(GameStorage.GetDirectory(storage, GameCatalog.Hoi4))
            .SaveAsync(new(SteamDirectory: fixture.Secondary), token);
        using var http = new HttpClient(new StoreHandler(_ => throw new HttpRequestException("Offline")));

        Directory.CreateDirectory(fixture.Steam);
        var catalog = await new SteamGameDiscovery(http, [], fixture.Home, storage).DiscoverAsync(fixture.Steam, token);
        var game = Assert.Single(catalog, candidate => candidate.AppId == 803003);
        var environment = await new SteamGameLocator(game, [], fixture.Home).DiscoverAsync(new(), token);
        var scan = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(environment, token);
        Assert.Equal("Pack", Assert.Single(scan.Mods).Name);
        Assert.Equal(Path.Combine(fixture.Secondary, "steamapps", "common", "External Game"), environment.GameDirectory);
    }

    [Fact]
    public async Task ManualModDirectoryWithTrailingSeparatorResolvesWrapperOnce()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        var folder = Path.Combine(fixture.Root, "custom-user", "mod");
        var content = fixture.File("custom-user/mod/Pack/data.txt", "mod");
        fixture.File("custom-user/mod/Pack.mod", "name=\"Pack\" path=\"mod/Pack\"");
        var game = new GameDefinition("steam-803004", 803004, "Generic", "Generic", "Generic", "Generic") { IsDiscovered = true };
        var environment = await new SteamGameLocator(game, [], fixture.Home)
            .DiscoverAsync(new(UserDataDirectory: folder + Path.DirectorySeparatorChar), token);
        var result = await new Ck3ModScanner(includeGenericContent: true).ScanAsync(environment, token);
        var mod = Assert.Single(result.Mods);
        Assert.Equal(Path.GetDirectoryName(content), mod.ContentPath);
        Assert.True(mod.IsAvailable);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task MonitorsTheFirstLocalModAndWorkshopDownloadForADiscoveredGame()
    {
        using var fixture = new SteamFixture();
        var token = TestContext.Current.CancellationToken;
        fixture.Install(804001, "First Mod");
        using var http = new HttpClient(new StoreHandler(_ => ("game", 30)));
        var discovery = new SteamGameDiscovery(http, [fixture.Steam], fixture.Home);
        var game = Assert.Single(await discovery.DiscoverAsync(cancellationToken: token), item => item.AppId == 804001);
        var locator = new SteamGameLocator(game, [fixture.Steam], fixture.Home);
        var monitor = new FileSystemLocalModMonitor(locator, new Ck3ModScanner(includeGenericContent: true), TimeSpan.FromMilliseconds(30));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var snapshots = monitor.WatchAsync(new(), deadline.Token).GetAsyncEnumerator(deadline.Token);

        Assert.True(await snapshots.MoveNextAsync());
        Assert.Empty(snapshots.Current.Mods);
        fixture.File("steam/steamapps/common/First Mod/Mods/Local Pack/pack.xml", "local pack");
        fixture.File("steam/steamapps/workshop/content/804001/123/content.pack", "workshop content");

        do
        {
            Assert.True(await snapshots.MoveNextAsync());
        }
        while (snapshots.Current.Mods.Count < 2);

        Assert.Contains(snapshots.Current.Mods, mod => mod.Name == "Local Pack");
        Assert.Contains(snapshots.Current.Mods, mod => mod.WorkshopId == 123);
        Assert.Single(snapshots.Current.Environment!.LocalModDirectories);
        Assert.Single(snapshots.Current.Environment.WorkshopDirectories);
    }

    private sealed class StoreHandler(Func<uint, (string Type, int Category)> response) : HttpMessageHandler
    {
        public ConcurrentBag<uint> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parameter = request.RequestUri!.Query.TrimStart('?').Split('&').Single(value => value.StartsWith("appids=", StringComparison.Ordinal));
            var appId = uint.Parse(parameter["appids=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Requests.Add(appId);
            var (type, category) = response(appId);
            var payload = new Dictionary<uint, object>
            {
                [appId] = new { success = true, data = new { type, categories = new[] { new { id = category } } } }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) });
        }
    }

    private sealed class SteamFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ck3-steam-discovery-{Guid.NewGuid():N}");
        public string Home => Path.Combine(Root, "home");
        public string Steam => Path.Combine(Root, "steam");
        public string Secondary => Path.Combine(Root, "secondary");

        public string File(string relative, string contents)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Install(uint appId, string name, string library = "steam", string flags = "4", uint? manifestId = null)
        {
            File($"{library}/steamapps/appmanifest_{appId}.acf",
                $$""" "AppState" { "appid" "{{manifestId ?? appId}}" "installdir" "{{name}}" "name" "{{name}}" "StateFlags" "{{flags}}" } """);
            File($"{library}/steamapps/common/{name}/game", "installed game");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
