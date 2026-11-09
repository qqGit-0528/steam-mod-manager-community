using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopDiskCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck3-workshop-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string CacheRoot => Path.Combine(_root, "cache", "v1");
    private FileWorkshopCacheStore Store(GameDefinition? game = null) => new(_root, _clock, game);

    public WorkshopDiskCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task PagesAndCompleteItemsSurviveANewStoreWithoutExtendingTheirAge()
    {
        var store = Store();
        var fetched = _clock.GetUtcNow();
        var item = Detail("完整介绍");
        var page = new CachedWorkshopPage(Page(item.Item), fetched);
        await store.WriteItemAsync(item, Token);
        await store.WritePageAsync("query/地图", page, Token);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var reopened = Store();
        var savedItem = Assert.IsType<CachedWorkshopItem>(await reopened.ReadItemAsync(123, Token));
        var savedPage = Assert.IsType<CachedWorkshopPage>(await reopened.ReadPageAsync("query/地图", Token));
        Assert.True(savedItem.HasFullDescription);
        Assert.Equal("完整介绍", savedItem.Item.Description);
        Assert.Equal(fetched, savedItem.FetchedAt);
        Assert.Equal(fetched, savedPage.FetchedAt);
        Assert.False(WorkshopCachePolicy.IsFresh(savedItem.FetchedAt, _clock.GetUtcNow()));
        Assert.Equal(123ul, Assert.Single(savedPage.Page.Items).PublishedFileId);
        _clock.Advance(TimeSpan.FromHours(24));
        Assert.Null(await reopened.ReadItemAsync(123, Token));
        Assert.Null(await reopened.ReadPageAsync("query/地图", Token));
    }

    [Fact]
    public async Task SummaryCannotDowngradeAnEqualRevisionOrRefreshTheDetailTimestamp()
    {
        var first = Store();
        var second = Store();
        var detail = Detail("完整介绍");
        await first.WriteItemAsync(detail, Token);
        Assert.NotNull(await second.ReadItemAsync(123, Token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.WriteItemAsync(detail with { Item = detail.Item with { Description = "摘要" }, FetchedAt = _clock.GetUtcNow(), HasFullDescription = false }, Token);

        var restored = Assert.IsType<CachedWorkshopItem>(await first.ReadItemAsync(123, Token));
        Assert.True(restored.HasFullDescription);
        Assert.Equal(detail.FetchedAt, restored.FetchedAt);
        Assert.Equal("完整介绍", restored.Item.Description);
    }

    [Fact]
    public async Task NewerSummaryInvalidatesTheOldRevisionDescription()
    {
        var store = Store();
        var detail = Detail("旧正文");
        await store.WriteItemAsync(detail, Token);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var next = detail with { Item = detail.Item with { Description = "新摘要", UpdatedAt = detail.Item.UpdatedAt.AddMinutes(1) }, FetchedAt = _clock.GetUtcNow(), HasFullDescription = false };
        await Store().WriteItemAsync(next, Token);
        var restored = Assert.IsType<CachedWorkshopItem>(await store.ReadItemAsync(123, Token));
        Assert.False(restored.HasFullDescription);
        Assert.Equal("新摘要", restored.Item.Description);
        Assert.Equal(next.Item.UpdatedAt, restored.Item.UpdatedAt);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesKeepTheNewestSourceRevision()
    {
        var detail = Detail("原始");
        var writes = Enumerable.Range(0, 20).Reverse().Select(i => Store().WriteItemAsync(detail with
        {
            Item = detail.Item with { Description = "修订 " + i, UpdatedAt = detail.Item.UpdatedAt.AddSeconds(i) }
        }, Token));
        await Task.WhenAll(writes);
        var restored = Assert.IsType<CachedWorkshopItem>(await Store().ReadItemAsync(123, Token));
        Assert.Equal("修订 19", restored.Item.Description);
        Assert.Empty(Directory.EnumerateFiles(CacheRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OlderPageWriteDoesNotReplaceTheNewerSnapshot()
    {
        var older = new CachedWorkshopPage(Page(Item() with { Title = "旧页面" }), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var newer = new CachedWorkshopPage(Page(Item() with { Title = "新页面" }), _clock.GetUtcNow());
        await Store().WritePageAsync("query", newer, Token);
        await Store().WritePageAsync("query", older, Token);
        var restored = Assert.IsType<CachedWorkshopPage>(await Store().ReadPageAsync("query", Token));
        Assert.Equal("新页面", Assert.Single(restored.Page.Items).Title);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("key")]
    [InlineData("app")]
    [InlineData("id")]
    [InlineData("payload-app")]
    [InlineData("future")]
    [InlineData("null-tags")]
    [InlineData("null-tag-element")]
    public async Task InvalidOrForeignItemDocumentsAreCacheMisses(string mutation)
    {
        await Store().WriteItemAsync(Detail("正文"), Token);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(CacheRoot, "items"), "*.json"));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        switch (mutation)
        {
            case "schema": root["schemaVersion"] = 99; break;
            case "key": root["key"] = "another-item"; break;
            case "app": root["appId"] = 570; break;
            case "id": root["data"]!["item"]!["publishedFileId"] = 456; break;
            case "payload-app": root["data"]!["item"]!["consumerAppId"] = 570; break;
            case "future": root["data"]!["fetchedAt"] = _clock.GetUtcNow().AddDays(1); break;
            case "null-tags": root["data"]!["item"]!["tags"] = null; break;
            case "null-tag-element": root["data"]!["item"]!["tags"] = new JsonArray((JsonNode?)null); break;
        }
        await File.WriteAllTextAsync(path, root.ToJsonString(), Token);
        Assert.Null(await Store().ReadItemAsync(123, Token));
    }

    [Fact]
    public async Task TruncatedAndOversizedDocumentsAreMissesAndOversizedWritesDoNotPersist()
    {
        await Store().WriteItemAsync(Detail("正文"), Token);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(CacheRoot, "items"), "*.json"));
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":", Token);
        Assert.Null(await Store().ReadItemAsync(123, Token));
        await File.WriteAllTextAsync(path, new string('x', 4 * 1024 * 1024 + 1), Token);
        Assert.Null(await Store().ReadItemAsync(123, Token));
        File.Delete(path);
        await Store().WriteItemAsync(Detail(new string('x', 5 * 1024 * 1024)), Token);
        Assert.Empty(Directory.GetFiles(Path.Combine(CacheRoot, "items"), "*.json"));
        Assert.Null(await Store().ReadItemAsync(123, Token));
    }

    [Fact]
    public async Task UnavailableDiskKeepsUsableMemoryAndDoesNotFailTheCaller()
    {
        var blocker = Path.Combine(_root, "blocked");
        await File.WriteAllTextAsync(blocker, "not a directory", Token);
        var store = new FileWorkshopCacheStore(blocker, _clock);
        await store.WriteItemAsync(Detail("可展示资料"), Token);
        var cached = Assert.IsType<CachedWorkshopItem>(await store.ReadItemAsync(123, Token));
        Assert.Equal("可展示资料", cached.Item.Description);
        Assert.Null(await new FileWorkshopCacheStore(blocker, _clock).ReadItemAsync(123, Token));
    }

    [Fact]
    public async Task UnavailableDiskCannotLetAnOlderPageReplaceNewerMemory()
    {
        var blocker = Path.Combine(_root, "blocked-pages");
        await File.WriteAllTextAsync(blocker, "not a directory", Token);
        var store = new FileWorkshopCacheStore(blocker, _clock);
        var older = new CachedWorkshopPage(Page(Item() with { Title = "旧页面" }), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var newer = new CachedWorkshopPage(Page(Item() with { Title = "新页面" }), _clock.GetUtcNow());
        await store.WritePageAsync("query", newer, Token);
        await store.WritePageAsync("query", older, Token);
        var restored = Assert.IsType<CachedWorkshopPage>(await store.ReadPageAsync("query", Token));
        Assert.Equal("新页面", Assert.Single(restored.Page.Items).Title);
    }

    [Fact]
    public async Task NullVersionTagInDiskPageIsAMissInsteadOfBreakingFiltering()
    {
        await Store().WritePageAsync("query", new CachedWorkshopPage(Page(Item()), _clock.GetUtcNow()), Token);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(CacheRoot, "pages"), "*.json"));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        root["data"]!["page"]!["versionTags"] = new JsonArray((JsonNode?)null);
        await File.WriteAllTextAsync(path, root.ToJsonString(), Token);
        Assert.Null(await Store().ReadPageAsync("query", Token));
    }

    [Fact]
    public async Task BoundGameAndEnvelopeKeepTwoGamesIndependent()
    {
        var ck3 = Detail("CK3");
        var hoi4 = ck3 with { Item = ck3.Item with { ConsumerAppId = GameCatalog.Hoi4.AppId, Description = "HOI4" } };
        await Store().WriteItemAsync(ck3, Token);
        await Store(GameCatalog.Hoi4).WriteItemAsync(hoi4, Token);
        await Store().WriteItemAsync(hoi4, Token);
        Assert.Equal("CK3", (await Store().ReadItemAsync(123, Token))!.Item.Description);
        Assert.Equal("HOI4", (await Store(GameCatalog.Hoi4).ReadItemAsync(123, Token))!.Item.Description);
    }

    [Fact]
    public async Task RemovalInvalidatesOtherInstancesMemoryAndTheDisk()
    {
        var first = Store();
        await first.WriteItemAsync(Detail("正文"), Token);
        Assert.NotNull(await first.ReadItemAsync(123, Token));
        await Store().RemoveItemAsync(123, Token);
        Assert.Null(await first.ReadItemAsync(123, Token));
        Assert.Null(await Store().ReadItemAsync(123, Token));
    }

    [Fact]
    public async Task DiskPageCapacityUsesAccessOrderInsteadOfRefreshingFetchedAt()
    {
        var store = Store();
        var writer = Store();
        var fetched = _clock.GetUtcNow();
        for (var i = 1; i <= WorkshopCacheLimits.DiskPages; i++)
        {
            await (i == 1 ? store : writer).WritePageAsync("query/" + i, new CachedWorkshopPage(Page(Item(), i), _clock.GetUtcNow()), Token);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        // 第一实例仍持有第 1 页的 L1；命中它也必须更新磁盘淘汰顺序。
        var touched = Assert.IsType<CachedWorkshopPage>(await store.ReadPageAsync("query/1", Token));
        Assert.Equal(fetched, touched.FetchedAt);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var nextPage = WorkshopCacheLimits.DiskPages + 1;
        await writer.WritePageAsync("query/" + nextPage, new CachedWorkshopPage(Page(Item(), nextPage), _clock.GetUtcNow()), Token);
        Assert.Equal(WorkshopCacheLimits.DiskPages, Directory.GetFiles(Path.Combine(CacheRoot, "pages"), "*.json").Length);
        Assert.NotNull(await Store().ReadPageAsync("query/1", Token));
        Assert.Null(await Store().ReadPageAsync("query/2", Token));
    }

    [Fact]
    public async Task DiskBudgetEvictsOldDocuments()
    {
        var folder = Path.Combine(CacheRoot, "items");
        Directory.CreateDirectory(folder);
        for (ulong id = 1; id <= 35; id++)
        {
            var key = $"steam/{Ck3.AppId}/{id}";
            var path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Ck3.AppId}/{key}"))).ToLowerInvariant() + ".json");
            var value = Detail(new string('x', 1024 * 1024)) with { Item = Item(id) with { Description = new string('x', 1024 * 1024) } };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, appId = Ck3.AppId, key, data = value }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Token);
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().AddSeconds((double)id - 100).UtcDateTime);
        }
        await Store().WriteItemAsync(Detail("最新"), Token);
        Assert.True(Directory.GetFiles(folder, "*.json").Sum(path => new FileInfo(path).Length) <= WorkshopCacheLimits.MetadataDiskBytes);
        Assert.NotNull(await Store().ReadItemAsync(123, Token));
    }

    [Fact]
    public async Task FirstReadWithinBudgetChecksOnlyItsTargetAndExplicitMaintenanceCleansExpiredDocuments()
    {
        var store = Store();
        await store.WriteItemAsync(Detail("即将过期"), Token);
        var itemPath = Assert.Single(Directory.GetFiles(Path.Combine(CacheRoot, "items"), "*.json"));
        _clock.Advance(WorkshopCachePolicy.KeepFor);
        var reopened = Store();
        Assert.Null(await reopened.ReadPageAsync("missing", Token));
        Assert.True(File.Exists(itemPath));
        Assert.Null(await reopened.ReadItemAsync(123, Token));
        await reopened.TrimAsync(Token);
        Assert.False(File.Exists(itemPath));
    }

    [Fact]
    public async Task FirstReadTrimsExistingOversizedCacheWithoutFetchingOrModifyingUserData()
    {
        var folder = Path.Combine(CacheRoot, "pages"); Directory.CreateDirectory(folder);
        var settings = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(settings, "用户设置保留", Token);
        for (var i = 1; i <= WorkshopCacheLimits.DiskPages + 4; i++)
        {
            var key = "query/" + i;
            var path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Ck3.AppId}/{key}"))).ToLowerInvariant() + ".json");
            var data = new CachedWorkshopPage(Page(Item(), i), _clock.GetUtcNow());
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, appId = Ck3.AppId, key, data }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Token);
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().AddSeconds(i - 100).UtcDateTime);
        }
        Assert.NotNull(await Store().ReadPageAsync("query/" + (WorkshopCacheLimits.DiskPages + 4), Token));
        Assert.Equal(WorkshopCacheLimits.DiskPages, Directory.GetFiles(folder, "*.json").Length);
        Assert.Equal("用户设置保留", await File.ReadAllTextAsync(settings, Token));
    }

    [Fact]
    public async Task SeparateItemFilesHaveACountLimitEvenWhenTheirTotalSizeIsSmall()
    {
        var store = Store();
        for (ulong id = 1; id <= WorkshopCacheLimits.DiskItems + 3; id++)
        {
            await store.WriteItemAsync(new(Item(id), _clock.GetUtcNow(), true), Token);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(WorkshopCacheLimits.DiskItems, Directory.GetFiles(Path.Combine(CacheRoot, "items"), "*.json").Length);
        Assert.Null(await Store().ReadItemAsync(1, Token));
        Assert.NotNull(await Store().ReadItemAsync((ulong)WorkshopCacheLimits.DiskItems + 3, Token));
    }

    [Fact]
    public async Task InterruptedCacheWritesAreRemovedDuringFirstReadMaintenance()
    {
        var pages = Path.Combine(CacheRoot, "pages"); var items = Path.Combine(CacheRoot, "items");
        Directory.CreateDirectory(pages); Directory.CreateDirectory(items);
        await File.WriteAllBytesAsync(Path.Combine(pages, "page.json.crash.tmp"), new byte[3 * 1024 * 1024], Token);
        await File.WriteAllBytesAsync(Path.Combine(items, "item.json.crash.tmp"), new byte[3 * 1024 * 1024], Token);
        Assert.Null(await Store().ReadPageAsync("missing", Token));
        Assert.Empty(Directory.EnumerateFiles(CacheRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationIsNotHiddenByBestEffortStorage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().WriteItemAsync(Detail("正文"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().ReadPageAsync("query", cancellation.Token));
    }

    private CachedWorkshopItem Detail(string description) => new(Item() with { Description = description }, _clock.GetUtcNow(), true);
    private static WorkshopItem Item(ulong id = 123) => new(id, Ck3.AppId, "测试 Mod", "摘要", null,
        new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), 100,
        ["1.20"], null, 1234, "author");
    private static WorkshopBrowsePage Page(WorkshopItem item, int page = 1) => new([item], page, 200, 200, ["Events"], ["1.20"]);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
