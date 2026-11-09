using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopSourceTests
{
    [Theory]
    [InlineData("hoi4")]
    [InlineData("eu4")]
    [InlineData("stellaris")]
    [InlineData("victoria3")]
    [InlineData("imperator")]
    public async Task SelectedGameAcceptsOwnConsumerAppAndRejectsCk3(string id)
    {
        var game = GameCatalog.Get(id);
        using var ownClient = Client(Item(appId: game.AppId));
        var item = await new SteamWorkshopSource(ownClient, game).GetItemAsync(123);
        Assert.Equal(game.AppId, item.ConsumerAppId);

        using var ck3Client = Client(Item());
        await Assert.ThrowsAsync<InvalidDataException>(() => new SteamWorkshopSource(ck3Client, game).GetItemAsync(123));
    }

    [Fact]
    public async Task UsesOfficialPostAndKeepsUnknownDependenciesNull()
    {
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(SteamWorkshopSource.Endpoint, request.RequestUri);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("publishedfileids%5B0%5D=123", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key=", body);
            return Json(Item());
        });
        using var client = new HttpClient(handler);
        var item = await new SteamWorkshopSource(client).GetItemAsync(123);
        Assert.Equal("测试 Mod", item.Title);
        Assert.Null(item.RequiredItemIds);
        Assert.Equal(Ck3.AppId, item.ConsumerAppId);
        Assert.Equal(["Events"], item.Tags);
    }

    [Theory]
    [InlineData(570u)]
    [InlineData(0u)]
    public async Task RejectsWrongConsumerApp(uint appId)
    {
        using var client = Client(Item(appId: appId));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
        Assert.Contains("不是 CK3", error.Message);
    }

    [Fact]
    public async Task AcceptsDifferentCreatorAppId()
    {
        using var client = Client(Item(creatorAppId: 766));
        Assert.Equal(123ul, (await new SteamWorkshopSource(client).GetItemAsync(123)).PublishedFileId);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(15)]
    public async Task ExplainsSteamFailureCode(int result)
    {
        using var client = Client(new { response = new { publishedfiledetails = new[] { new { result } } } });
        var error = await Assert.ThrowsAsync<WorkshopItemUnavailableException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
        Assert.Contains(result.ToString(), error.Message);
        Assert.Equal(123UL, error.PublishedFileId);
    }

    [Fact]
    public async Task TemporarySteamFailureIsNotTreatedAsDeletedOrPrivate()
    {
        using var client = Client(new { response = new { publishedfiledetails = new[] { new { result = 20 } } } });
        await Assert.ThrowsAsync<InvalidDataException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
    }

    [Fact]
    public async Task MissingFieldsProduceReadableError()
    {
        using var client = Client(new { response = new { publishedfiledetails = new[] { new { result = 1, publishedfileid = "123" } } } });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
        Assert.Contains("consumer_app_id", error.Message);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Item());
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SteamWorkshopSource(client).GetItemAsync(123, cancellation.Token));
    }

    [Fact]
    public async Task TransportTimeoutIsReadable()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new TaskCanceledException()));
        await Assert.ThrowsAsync<TimeoutException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
    }

    [Fact]
    public async Task HttpFailureIsNotDeserialized()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new SteamWorkshopSource(client).GetItemAsync(123));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    private static object Item(uint appId = Ck3.AppId, uint creatorAppId = Ck3.AppId) => new
    {
        response = new
        {
            publishedfiledetails = new[]
            {
                new { result = 1, publishedfileid = "123", consumer_app_id = appId, creator_app_id = creatorAppId,
                    title = "测试 Mod", description = "文本", time_updated = 1_700_000_000, file_size = "4096",
                    tags = new[] { new { tag = "Events" } } }
            }
        }
    };
    private static HttpClient Client(object content) => new(new StubHandler((_, _) => Task.FromResult(Json(content))));
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

public sealed class LocatorTests
{
    [Fact]
    public async Task PrisonArchitectUsesTheModsDirectoryAndWatchesItBeforeTheFirstInstallation()
    {
        using var fixture = new TempDirectory();
        var user = fixture.Directory("userdata");
        var result = await new SteamGameLocator(GameCatalog.PrisonArchitect, [], fixture.Root)
            .DiscoverAsync(new(UserDataDirectory: user), TestContext.Current.CancellationToken);

        var mods = Path.Combine(user, "mods");
        Assert.Equal(mods, Assert.Single(result.LocalModDirectories));
        Assert.Contains(mods, result.LocalModWatchPaths);
        Assert.False(Directory.Exists(mods));
    }

    [Theory]
    [InlineData("ck3")]
    [InlineData("hoi4")]
    [InlineData("eu4")]
    [InlineData("stellaris")]
    [InlineData("victoria3")]
    [InlineData("imperator")]
    public async Task DiscoveryUsesOnlySelectedGamesManifestWorkshopAndUserDirectory(string id)
    {
        using var fixture = new TempDirectory();
        var steam = fixture.Directory("steam");
        foreach (var candidate in GameCatalog.All)
        {
            fixture.File($"steam/steamapps/appmanifest_{candidate.AppId}.acf",
                $"\"AppState\" {{ \"appid\" \"{candidate.AppId}\" \"installdir\" \"{candidate.Id}\" }}");
            fixture.File($"steam/steamapps/common/{candidate.Id}/binaries/{candidate.ExecutableName}.exe", "");
            fixture.File($"steam/steamapps/common/{candidate.Id}/{candidate.ExecutableName}.exe", "");
            fixture.Directory($"steam/steamapps/workshop/content/{candidate.AppId}");
        }
        var game = GameCatalog.Get(id);

        var result = await new SteamGameLocator(game, [steam], fixture.Root).DiscoverAsync(new());

        Assert.Equal(fixture.Path($"steam/steamapps/common/{id}"), result.GameDirectory);
        Assert.Equal(new[] { fixture.Path($"steam/steamapps/workshop/content/{game.AppId}") }, result.WorkshopDirectories);
        Assert.Equal(game.GameName, Path.GetFileName(result.UserDataDirectory));
    }

    [Fact]
    public async Task SelectedGameRejectsAChangedManifestAppIdAndOtherGamesManualDirectory()
    {
        using var fixture = new TempDirectory();
        var steam = fixture.Directory("steam");
        var hoi4 = GameCatalog.Hoi4;
        fixture.File($"steam/steamapps/appmanifest_{hoi4.AppId}.acf",
            $"\"AppState\" {{ \"appid\" \"{Ck3.AppId}\" \"installdir\" \"hoi4\" }}");
        fixture.File("steam/steamapps/common/hoi4/hoi4.exe", "");
        fixture.File("manual-ck3/binaries/ck3.exe", "");

        var result = await new SteamGameLocator(hoi4, [steam], fixture.Root).DiscoverAsync(
            new(GameDirectory: fixture.Path("manual-ck3")));

        Assert.Null(result.GameDirectory);
        Assert.Contains(result.Warnings, warning => warning.Contains("App ID"));
    }

    [Fact]
    public async Task FindsGameAndWorkshopInSecondVdfLibraryWithoutGuessingGameVersion()
    {
        using var fixture = new TempDirectory();
        var steam = fixture.Directory("steam");
        var second = fixture.Directory("library B");
        fixture.File("steam/steamapps/libraryfolders.vdf", "\uFEFF\"libraryfolders\" { \"0\" { \"path\" \"" + Escape(steam) + "\" } \"1\" { \"path\" \"" + Escape(second) + "\" } }");
        fixture.File("library B/steamapps/appmanifest_1158310.acf", "\"AppState\" { \"appid\" \"1158310\" \"installdir\" \"Crusader Kings III\" \"buildid\" \"987654321\" }");
        var game = fixture.Directory("library B/steamapps/common/Crusader Kings III");
        fixture.File("library B/steamapps/common/Crusader Kings III/binaries/ck3", "");
        var workshop = fixture.Directory("library B/steamapps/workshop/content/1158310");
        var result = await new SteamGameLocator([steam], fixture.Root).DiscoverAsync(new());
        Assert.Equal(game, result.GameDirectory);
        Assert.Equal([workshop], result.WorkshopDirectories);
        Assert.Null(result.GameVersion);
        Assert.Contains(result.Warnings, w => w.Contains("buildid"));
    }

    [Fact]
    public async Task HonorsManualDirectoriesAndReadsRealVersionFile()
    {
        using var fixture = new TempDirectory();
        var game = fixture.Directory("manual game");
        fixture.File("manual game/binaries/ck3.exe", "");
        fixture.File("manual game/launcher-settings.json", "{\"version\":\"1.12.5 (Test Codename)\"}");
        var userData = fixture.Directory("manual documents");
        var result = await new SteamGameLocator([], fixture.Root).DiscoverAsync(new(GameDirectory: game, UserDataDirectory: userData));
        Assert.Equal(game, result.GameDirectory);
        Assert.Equal(userData, result.UserDataDirectory);
        Assert.Equal("1.12.5", result.GameVersion);
    }

    [Fact]
    public async Task MissingInstallationReturnsWarnings()
    {
        using var fixture = new TempDirectory();
        var result = await new SteamGameLocator([], fixture.Root).DiscoverAsync(new());
        Assert.Null(result.GameDirectory);
        Assert.Null(result.GameVersion);
        Assert.Empty(result.WorkshopDirectories);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("binaries/ck3.app/Contents/MacOS/ck3")]
    [InlineData("ck3.app/Contents/MacOS/ck3")]
    public async Task RecognizesMacApplicationBundles(string binary)
    {
        using var fixture = new TempDirectory();
        var game = fixture.Directory("mac game");
        fixture.File("mac game/" + binary, "");
        var result = await new SteamGameLocator([], fixture.Root).DiscoverAsync(new(GameDirectory: game));
        Assert.Equal(game, result.GameDirectory);
    }

    [Fact]
    public async Task SupportsOldVdfLibraryFormatAndToleratesBrokenManifest()
    {
        using var fixture = new TempDirectory();
        var steam = fixture.Directory("steam");
        var library = fixture.Directory("other");
        fixture.File("steam/steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"1\" \"" + Escape(library) + "\" }");
        fixture.File("other/steamapps/appmanifest_1158310.acf", "\"AppState\" { \"appid\" \"1158310\"");
        var workshop = fixture.Directory("other/steamapps/workshop/content/1158310");
        var result = await new SteamGameLocator([steam], fixture.Root).DiscoverAsync(new());
        Assert.Contains(workshop, result.WorkshopDirectories);
        Assert.Contains(result.Warnings, w => w.Contains("安装清单"));
    }
    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class ScannerTests
{
    [Fact]
    public async Task ParsesBomCommentsEscapedNamesAndDependenciesOnly()
    {
        using var fixture = new TempDirectory();
        fixture.Directory("userdata/mod/local");
        fixture.File("userdata/mod/local.mod", "\uFEFF# header\nname=\"外层\"\npath=\"mod/local\"\n");
        fixture.File("userdata/mod/local/descriptor.mod", "name=\"测试 \\\"引号\\\" #内容\" // comment\nsupported_version=\"1.12.*\"\nversion=\"2.0\"\ndependencies={\"基础包\" \"另一个包\"}\nreplace_path=\"common/traits\"\n");
        var result = await new Ck3ModScanner().ScanAsync(fixture.Path("userdata"), []);
        var mod = Assert.Single(result.Mods);
        Assert.Equal("测试 \"引号\" #内容", mod.Name);
        Assert.Equal("1.12.*", mod.SupportedGameVersion);
        Assert.Equal(["基础包", "另一个包"], mod.Dependencies);
        Assert.True(mod.IsAvailable);
        Assert.Equal(ModOrigin.Local, mod.Origin);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task WorkshopWrappersAndContentAreDeduplicatedAndStable()
    {
        using var fixture = new TempDirectory();
        var workshop = fixture.Directory("workshop/content/1158310");
        var content = fixture.Directory("workshop/content/1158310/123456");
        fixture.File("workshop/content/1158310/123456/descriptor.mod", "name=\"工坊Mod\" supported_version=\"1.12.*\"");
        fixture.File("userdata/mod/ugc_123456.mod", "name=\"旧名称\" remote_file_id=\"123456\" path=\"" + content.Replace("\\", "\\\\") + "\"");
        var scanner = new Ck3ModScanner();
        var first = Assert.Single((await scanner.ScanAsync(fixture.Path("userdata"), [workshop])).Mods);
        Assert.Equal((ulong)123456, first.WorkshopId);
        Assert.Equal("工坊Mod", first.Name);
        System.IO.File.Delete(fixture.Path("userdata/mod/ugc_123456.mod"));
        var second = Assert.Single((await scanner.ScanAsync(fixture.Path("userdata"), [workshop])).Mods);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ContentPath, second.ContentPath);
    }

    [Fact]
    public async Task DifferentLocalVersionsOfOneWorkshopProjectStayDistinct()
    {
        using var fixture = new TempDirectory();
        var workshop = fixture.Directory("workshop/content/1158310");
        fixture.Directory("userdata/mod/backup");
        fixture.File("userdata/mod/backup.mod", "name=\"备份\" path=\"mod/backup\" remote_file_id=\"123456\" version=\"1\"");
        fixture.File("workshop/content/1158310/123456/descriptor.mod", "name=\"新版\" remote_file_id=\"123456\" version=\"2\"");
        var result = await new Ck3ModScanner().ScanAsync(fixture.Path("userdata"), [workshop]);
        Assert.Equal(2, result.Mods.Count);
        Assert.All(result.Mods, mod => Assert.Equal((ulong)123456, mod.WorkshopId));
        Assert.Equal(2, result.Mods.Select(mod => mod.Id).Distinct().Count());
    }

    [Fact]
    public async Task BrokenAndMissingDescriptorsDoNotAbortOtherMods()
    {
        using var fixture = new TempDirectory();
        fixture.File("userdata/mod/bad.mod", "name=\"unterminated");
        fixture.File("userdata/mod/missing.mod", "name=\"缺失\" path=\"mod/missing\"");
        fixture.Directory("userdata/mod/good");
        fixture.File("userdata/mod/good.mod", "name=\"正常\" path=\"mod/good\"");
        var workshop = fixture.Directory("workshop/1158310");
        fixture.Directory("workshop/1158310/123456");
        var result = await new Ck3ModScanner().ScanAsync(fixture.Path("userdata"), [workshop]);
        Assert.Equal(2, result.Mods.Count);
        Assert.False(Assert.Single(result.Mods, m => m.Name == "缺失").IsAvailable);
        Assert.True(Assert.Single(result.Mods, m => m.Name == "正常").IsAvailable);
        Assert.Contains(result.Warnings, w => w.Contains("引号"));
        Assert.Contains(result.Warnings, w => w.Contains("内容不存在"));
        Assert.Contains(result.Warnings, w => w.Contains("缺少 descriptor.mod"));
    }

    [Fact]
    public async Task RelativeBackslashPathAndRenamedModRetainLocalId()
    {
        using var fixture = new TempDirectory();
        fixture.Directory("userdata/mod/example");
        fixture.File("userdata/mod/example.mod", "name=\"原名称\" path=\"mod\\\\example\"");
        var scanner = new Ck3ModScanner();
        var first = Assert.Single((await scanner.ScanAsync(fixture.Path("userdata"), [])).Mods);
        fixture.File("userdata/mod/example.mod", "name=\"新名称\" path=\"mod/example\"");
        var second = Assert.Single((await scanner.ScanAsync(fixture.Path("userdata"), [])).Mods);
        Assert.True(first.IsAvailable);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task PathMustBeDirectoryButArchiveMayBeFile()
    {
        using var fixture = new TempDirectory();
        fixture.File("userdata/mod/file.zip", "archive fixture");
        fixture.File("userdata/mod/not-directory.mod", "name=\"错误路径\" path=\"mod/file.zip\"");
        var scanner = new Ck3ModScanner();
        var result = await scanner.ScanAsync(fixture.Path("userdata"), []);
        Assert.False(Assert.Single(result.Mods).IsAvailable);
        fixture.File("userdata/mod/archive.mod", "name=\"压缩包\" archive=\"mod/file.zip\"");
        result = await scanner.ScanAsync(fixture.Path("userdata"), []);
        // Both descriptors resolve to the same content path, so the stable ID removes duplicates.
        // The unavailable wrapper must not hide an available archive.
        Assert.True(Assert.Single(result.Mods).IsAvailable);
    }
}

public sealed class JsonStoreTests
{
    [Fact]
    public async Task GameStoragePreservesLegacyCk3FilesAndSeparatesOtherGamesSettings()
    {
        using var fixture = new TempDirectory();
        var legacy = new JsonSettingsStore(fixture.Root);
        var ck3Settings = new AppSettings(GameDirectory: "ck3-install");
        await legacy.SaveAsync(ck3Settings);

        var ck3Directory = GameStorage.GetDirectory(fixture.Root, GameCatalog.Ck3);
        var hoi4Directory = GameStorage.GetDirectory(fixture.Root, GameCatalog.Hoi4);
        Assert.Equal(fixture.Root, ck3Directory);
        Assert.Equal(fixture.Path("games/hoi4"), hoi4Directory);
        Assert.Equal(ck3Settings, await new JsonSettingsStore(ck3Directory).LoadAsync());
        Assert.Equal(new AppSettings(), await new JsonSettingsStore(hoi4Directory).LoadAsync());

        await new JsonSettingsStore(hoi4Directory).SaveAsync(new AppSettings(GameDirectory: "hoi4-install"));
        Assert.Equal(ck3Settings, await legacy.LoadAsync());
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(game => GameStorage.GetDirectory(fixture.Root, game)).Distinct().Count());
    }

    [Fact]
    public async Task ProfileOrderAndModOrderSurviveReloadAndUpdate()
    {
        using var fixture = new TempDirectory();
        var store = new JsonProfileStore(fixture.Root);
        var a = Profile("第一套", ["three", "one", "two"]);
        var b = Profile("第二套", ["other"]);
        await store.SaveAsync(a);
        await store.SaveAsync(b);
        await store.SaveAsync(a with { Name = "更新名称" });
        var loaded = await new JsonProfileStore(fixture.Root).LoadAsync();
        Assert.Equal([a.Id, b.Id], loaded.Select(p => p.Id));
        Assert.Equal(["three", "one", "two"], loaded[0].Mods.Select(m => m.InstalledModId));
        Assert.Equal("更新名称", loaded[0].Name);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task DeletingProfileRemovesOnlyThatEntryAndIsIdempotent()
    {
        using var fixture = new TempDirectory();
        var store = new JsonProfileStore(fixture.Root);
        var a = Profile("第一套", ["one"]);
        var b = Profile("第二套", []);
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        await store.DeleteAsync(a.Id);
        await store.DeleteAsync(a.Id);

        var loaded = await new JsonProfileStore(fixture.Root).LoadAsync();
        Assert.Equal([b.Id], loaded.Select(p => p.Id));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task DeletingMissingProfileDoesNotCreateOrRewriteFile()
    {
        using var fixture = new TempDirectory();
        var store = new JsonProfileStore(fixture.Root);
        await store.DeleteAsync(Guid.NewGuid());
        Assert.False(System.IO.File.Exists(fixture.Path("profiles.json")));
    }

    [Fact]
    public async Task CorruptProfilesArePreservedWhenSaveIsAttempted()
    {
        using var fixture = new TempDirectory();
        fixture.File("profiles.json", "{broken json");
        var store = new JsonProfileStore(fixture.Root);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Contains("原文件已保留", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Profile("测试", [])));
        Assert.Equal("{broken json", await System.IO.File.ReadAllTextAsync(fixture.Path("profiles.json")));
    }

    [Fact]
    public async Task SettingsRoundTripAndCorruptionProtection()
    {
        using var fixture = new TempDirectory();
        var store = new JsonSettingsStore(fixture.Root);
        Assert.Equal(new AppSettings(), await store.LoadAsync());
        var settings = new AppSettings("steam", "game", "userdata");
        await store.SaveAsync(settings);
        Assert.Equal(settings, await new JsonSettingsStore(fixture.Root).LoadAsync());
        fixture.File("settings.json", "null");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings));
        Assert.Equal("null", await System.IO.File.ReadAllTextAsync(fixture.Path("settings.json")));
    }

    private static ModProfile Profile(string name, string[] ids) => new(Guid.NewGuid(), name, "1.12.5",
        ids.Select(id => new ProfileMod(id, id, "/mods/" + id, null, "1.12.*")).ToArray(), DateTimeOffset.UtcNow);
}

public sealed class NavigatorTests
{
    [Theory]
    [InlineData("ck3")]
    [InlineData("hoi4")]
    [InlineData("eu4")]
    [InlineData("stellaris")]
    [InlineData("victoria3")]
    [InlineData("imperator")]
    public void LaunchAndWorkshopHomeUseTheSelectedGamesSteamIdentity(string id)
    {
        var game = GameCatalog.Get(id);
        var requests = new List<ProcessStartInfo>();
        var navigator = new ExternalNavigator(game, requests.Add);

        navigator.LaunchGame();
        navigator.OpenWorkshop();

        Assert.Equal($"steam://rungameid/{game.AppId}", requests[0].FileName);
        Assert.Equal($"steam://openurl/https://steamcommunity.com/app/{game.AppId}/workshop/", requests[1].FileName);
        Assert.All(requests, request => Assert.True(request.UseShellExecute));
    }

    [Fact]
    public void LaunchGameUsesFixedCk3SteamUri()
    {
        ProcessStartInfo? captured = null;
        var navigator = new ExternalNavigator(info => captured = info);

        navigator.LaunchGame();

        Assert.Equal("steam://rungameid/1158310", captured!.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Empty(captured.ArgumentList);
    }

    [Fact]
    public void OnlyBuildsFixedSteamUris()
    {
        ProcessStartInfo? captured = null;
        var navigator = new ExternalNavigator(info => captured = info);
        navigator.OpenWorkshop(123456);
        Assert.Equal("steam://url/CommunityFilePage/123456", captured!.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Throws<ArgumentOutOfRangeException>(() => navigator.OpenWorkshop(0));
    }

    [Fact]
    public void DirectoryNamesAreArgumentsNotShellCommands()
    {
        using var fixture = new TempDirectory();
        var path = fixture.Directory("mods; echo injected");
        ProcessStartInfo? captured = null;
        var navigator = new ExternalNavigator(info => captured = info);
        navigator.OpenDirectory(path);
        Assert.False(captured!.UseShellExecute);
        Assert.Equal([path], captured.ArgumentList);
        Assert.Throws<ArgumentException>(() => navigator.OpenDirectory("https://example.com"));
        Assert.Throws<DirectoryNotFoundException>(() => navigator.OpenDirectory(fixture.Path("absent")));
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck3-infrastructure-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => System.IO.Directory.CreateDirectory(Root);
    public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string Directory(string relative)
    {
        var path = Path(relative);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
    public void File(string relative, string text)
    {
        var path = Path(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }
    public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
}
