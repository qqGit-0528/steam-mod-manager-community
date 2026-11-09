using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkshopCacheFlowTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchCachesOnlyTheRequestedPageWithoutFetchingOrCopyingEveryItemsDetails()
    {
        var clock = new Clock(); var cache = new MemoryWorkshopCacheStore();
        var source = new Source((_, _) => Task.FromResult(Item(description: "已打开的完整正文")));
        var browser = new Browser(request => Page(request, Item()));
        var service = new WorkshopService(source, browser, cache: cache, timeProvider: clock);
        var request = new WorkshopSearchRequest("地图");
        await Collect(service.ObserveSearchAsync(request, cancellationToken: Token));
        Assert.Equal(1, browser.Calls);
        Assert.Equal(0, source.Calls);
        Assert.NotNull(await cache.ReadPageAsync(WorkshopCachePolicy.SearchKey(request), Token));
        Assert.Null(await cache.ReadPageAsync(WorkshopCachePolicy.SearchKey(request with { Page = 2 }), Token));
        Assert.Null(await cache.ReadItemAsync(123, Token));
        await service.LookupAsync("123", Token);
        Assert.True((await cache.ReadItemAsync(123, Token))!.HasFullDescription);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task SearchRefreshDoesNotExtendTheTimestampOfAnUnchangedDetail()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var item = Item(description: "旧列表摘要");
        var cached = new CachedWorkshopItem(item, clock.GetUtcNow(), false);
        await cache.WriteItemAsync(cached, Token);
        clock.Advance(TimeSpan.FromMinutes(1));
        var service = new WorkshopService(new Source((_, _) => throw new InvalidOperationException()),
            new Browser(request => Page(request, item with { Description = "同修订的摘要" })),
            cache: cache, timeProvider: clock);
        await Collect(service.ObserveSearchAsync(new(), forceRefresh: true, cancellationToken: Token));
        Assert.Same(cached, await cache.ReadItemAsync(item.PublishedFileId, Token));
    }

    [Fact]
    public async Task SearchAndRefreshUseOnlyPageCacheEvenWhenAListRevisionChanges()
    {
        var clock = new Clock(); var cache = new MemoryWorkshopCacheStore();
        var old = Item(description: "旧完整正文");
        await cache.WriteItemAsync(new(old, clock.GetUtcNow(), true), Token);
        var newer = old with { UpdatedAt = old.UpdatedAt.AddDays(1), Description = "新列表摘要" };
        var cold = newer with { PublishedFileId = 456 };
        var offline = false;
        var browser = new Browser(request => offline ? throw new IOException("离线") :
            new([newer, cold], request.Page, 3, 2, [], []));
        var source = new Source((_, _) => throw new InvalidOperationException("搜索不应补查详情"));
        var service = new WorkshopService(source, browser, cache: new PageOnlyCache(cache), timeProvider: clock);
        var fetched = Assert.Single(await Collect(service.ObserveSearchAsync(new(), cancellationToken: Token)));
        var hit = Assert.Single(await Collect(service.ObserveSearchAsync(new(), cancellationToken: Token)));
        var refreshed = await Collect(service.ObserveSearchAsync(new(), forceRefresh: true, cancellationToken: Token));
        Assert.Equal(2, refreshed.Count);
        Assert.All(new[] { fetched, hit, refreshed[^1] }, update =>
        {
            Assert.Null(update.Error);
            Assert.Equal(newer, update.Page!.Items[0]);
        });
        offline = true;
        var fallback = await Collect(service.ObserveSearchAsync(new(), forceRefresh: true, cancellationToken: Token));
        Assert.NotNull(fallback[^1].Error);
        Assert.Equal(newer, fallback[^1].Page!.Items[0]);
        Assert.Equal(3, browser.Calls);
        Assert.Equal(0, source.Calls);
        Assert.Equal(old, (await cache.ReadItemAsync(123, Token))!.Item);
        Assert.Null(await cache.ReadItemAsync(456, Token));
    }

    [Fact]
    public async Task ListContextIsAvailableBeforeAnyDetailRequestCompletes()
    {
        var clock = new Clock();
        var reply = new TaskCompletionSource<WorkshopItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source((_, token) => reply.Task.WaitAsync(token));
        var service = new WorkshopService(source, timeProvider: clock);
        var summary = Item(description: "列表简介");
        await using var updates = service.ObserveDetailAsync("123", summary, cancellationToken: Token).GetAsyncEnumerator(Token);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("列表简介", updates.Current.Item!.Description);
        Assert.False(updates.Current.HasFullDescription);
        Assert.True(updates.Current.IsRefreshing);
        Assert.Equal(0, source.Calls);

        var pending = updates.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        reply.SetResult(summary with { Description = "完整正文" });
        Assert.True(await pending);
        Assert.Equal("完整正文", updates.Current.Item!.Description);
        Assert.True(updates.Current.HasFullDescription);
        Assert.False(updates.Current.IsRefreshing);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task FreshCompleteDetailIsReusedWithoutLookup()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        await cache.WriteItemAsync(new(Item(description: "已保存正文"), clock.GetUtcNow(), true), Token);
        var source = new Source((_, _) => throw new InvalidOperationException("不应请求"));
        var service = new WorkshopService(source, cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        var hit = Assert.Single(updates);
        Assert.Equal("已保存正文", hit.Item!.Description);
        Assert.True(hit.HasFullDescription);
        Assert.False(hit.IsStale);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task ReadingBeforeExpiryDoesNotExtendTheTwoMinuteDeadline()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var original = Item(description: "旧正文");
        await cache.WriteItemAsync(new(original, clock.GetUtcNow(), true), Token);
        var source = new Source((_, _) => Task.FromResult(original with { Description = "新正文" }));
        var service = new WorkshopService(source, cache: cache, timeProvider: clock);
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Single(await Collect(service.ObserveDetailAsync("123", cancellationToken: Token)));
        Assert.Equal(0, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        var updates = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.True(updates[0].IsStale);
        Assert.True(updates[0].IsRefreshing);
        Assert.Equal("新正文", updates[^1].Item!.Description);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ANonEmptySummaryStillNeedsTheFullDescription()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        await cache.WriteItemAsync(new(Item(description: "有内容的摘要"), clock.GetUtcNow(), false), Token);
        var source = new Source((_, _) => Task.FromResult(Item(description: "作者完整正文")));
        var service = new WorkshopService(source, cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.False(updates[0].HasFullDescription);
        Assert.True(updates[^1].HasFullDescription);
        Assert.Equal("作者完整正文", updates[^1].Item!.Description);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task SameRevisionSummaryCannotDowngradeOrRenewACompleteDetail()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var fetched = clock.GetUtcNow();
        await cache.WriteItemAsync(new(Item(description: "完整正文"), fetched, true), Token);
        clock.Advance(TimeSpan.FromMinutes(3));
        var browser = new Browser(request => Page(request, Item(description: "列表摘要")));
        var service = new WorkshopService(new Source((_, _) => throw new IOException("离线")), browser,
            cache: cache, timeProvider: clock);
        var pages = await Collect(service.ObserveSearchAsync(new(), cancellationToken: Token));
        Assert.Equal("列表摘要", Assert.Single(pages[^1].Page!.Items).Description);
        var stored = (await cache.ReadItemAsync(123, Token))!;
        Assert.True(stored.HasFullDescription);
        Assert.Equal(fetched, stored.FetchedAt);
        var details = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.True(details[^1].IsStale);
        Assert.NotNull(details[^1].Error);
    }

    [Fact]
    public async Task ANewerSummaryRequiresAFullDetailForThatRevision()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var old = Item(description: "旧正文");
        await cache.WriteItemAsync(new(old, clock.GetUtcNow(), true), Token);
        var newer = old with { Description = "新摘要", UpdatedAt = old.UpdatedAt.AddSeconds(1) };
        var service = new WorkshopService(new Source((_, _) => Task.FromResult(newer with { Description = "新正文" })),
            new Browser(request => Page(request, newer)), cache: cache, timeProvider: clock);
        await Collect(service.ObserveSearchAsync(new(), cancellationToken: Token));
        Assert.Equal(old, (await cache.ReadItemAsync(123, Token))!.Item);
        var updates = await Collect(service.ObserveDetailAsync("123", newer, cancellationToken: Token));
        Assert.Equal("新正文", updates[^1].Item!.Description);
        Assert.True(updates[^1].HasFullDescription);
    }

    [Fact]
    public async Task OlderNetworkDetailCannotReplaceTheNewerSelectedPublication()
    {
        var newer = Item(description: "新列表资料") with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var service = new WorkshopService(new Source((_, _) => Task.FromResult(Item(description: "较早正文"))));
        var updates = await Collect(service.ObserveDetailAsync("123", newer, cancellationToken: Token));
        Assert.Equal(newer, updates[^1].Item);
        Assert.False(updates[^1].HasFullDescription);
        Assert.NotNull(updates[^1].Error);
    }

    [Fact]
    public async Task OlderNetworkRevisionCannotMakeExpiredCachedDetailsFresh()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var newer = Item(description: "较新但过期的正文") with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var fetchedAt = clock.GetUtcNow();
        await cache.WriteItemAsync(new(newer, fetchedAt, true), Token);
        clock.Advance(TimeSpan.FromMinutes(3));
        var service = new WorkshopService(new Source((_, _) => Task.FromResult(Item(description: "来源旧修订"))),
            cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.True(updates[^1].IsStale);
        Assert.NotNull(updates[^1].Error);
        Assert.Equal(newer, updates[^1].Item);
        Assert.Equal(fetchedAt, updates[^1].FetchedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LookupAsync("123", Token));
    }

    [Fact]
    public async Task FailedRefreshKeepsExpiredDetailButDoesNotRenewItsTimestamp()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var fetched = clock.GetUtcNow();
        await cache.WriteItemAsync(new(Item(description: "仍可阅读"), fetched, true), Token);
        clock.Advance(TimeSpan.FromMinutes(3));
        var service = new WorkshopService(new Source((_, _) => throw new HttpRequestException("暂时离线")), cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.Equal("仍可阅读", updates[^1].Item!.Description);
        Assert.True(updates[^1].IsStale);
        Assert.False(updates[^1].IsRefreshing);
        Assert.Contains("暂时离线", updates[^1].Error);
        Assert.Equal(fetched, (await cache.ReadItemAsync(123, Token))!.FetchedAt);
    }

    [Fact]
    public async Task ConcurrentNewerSummarySurvivesAFailedOlderDetailRefresh()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var original = Item(description: "旧修订完整正文");
        await cache.WriteItemAsync(new(original, clock.GetUtcNow(), true), Token);
        clock.Advance(TimeSpan.FromMinutes(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorkshopItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WorkshopService(new Source((_, token) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        }), cache: cache, timeProvider: clock);
        var loading = Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        await entered.Task.WaitAsync(Token);
        var newer = original with { Description = "刷新期间到达的新摘要", UpdatedAt = original.UpdatedAt.AddDays(1) };
        await cache.WriteItemAsync(new(newer, clock.GetUtcNow(), false), Token);
        release.SetResult(original);
        var updates = await loading;
        Assert.Equal(newer, updates[^1].Item);
        Assert.False(updates[^1].HasFullDescription);
        Assert.True(updates[^1].IsStale);
        Assert.NotNull(updates[^1].Error);
    }

    [Fact]
    public async Task MoreThanADayOldDetailsAreNotShown()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        await cache.WriteItemAsync(new(Item(description: "超过一天"), clock.GetUtcNow(), true), Token);
        clock.Advance(TimeSpan.FromHours(24));
        var service = new WorkshopService(new Source((_, _) => Task.FromResult(Item(description: "重新获取"))), cache: cache, timeProvider: clock);
        var update = Assert.Single(await Collect(service.ObserveDetailAsync("123", cancellationToken: Token)));
        Assert.Equal("重新获取", update.Item!.Description);
    }

    [Fact]
    public async Task ExpiredPageIsEmittedBeforeItsRefreshAndQueriesStayIndependent()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        var request = new WorkshopSearchRequest("地图");
        await cache.WritePageAsync(WorkshopCachePolicy.SearchKey(request), new(Page(request, Item(description: "旧页面")), clock.GetUtcNow()), Token);
        clock.Advance(TimeSpan.FromMinutes(3));
        var browser = new Browser(query => Page(query, Item(description: "新页面")));
        var service = new WorkshopService(new Source((_, _) => throw new InvalidOperationException()), browser, cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveSearchAsync(request, cancellationToken: Token));
        Assert.True(updates[0].IsStale);
        Assert.Equal("旧页面", Assert.Single(updates[0].Page!.Items).Description);
        Assert.Equal("新页面", Assert.Single(updates[^1].Page!.Items).Description);
        Assert.Single(await Collect(service.ObserveSearchAsync(request, cancellationToken: Token)));
        await Collect(service.ObserveSearchAsync(request with { Page = 2 }, cancellationToken: Token));
        Assert.Equal(2, browser.Calls);
    }

    [Fact]
    public async Task ForceRefreshRechecksEvenFreshDetails()
    {
        var clock = new Clock();
        var cache = new MemoryWorkshopCacheStore();
        await cache.WriteItemAsync(new(Item(description: "缓存"), clock.GetUtcNow(), true), Token);
        var source = new Source((_, _) => Task.FromResult(Item(description: "主动更新")));
        var service = new WorkshopService(source, cache: cache, timeProvider: clock);
        var updates = await Collect(service.ObserveDetailAsync("123", forceRefresh: true, cancellationToken: Token));
        Assert.True(updates[0].IsRefreshing);
        Assert.Equal("主动更新", updates[^1].Item!.Description);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task CancelingOneReaderDoesNotCancelAnotherReadersSharedRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<WorkshopItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCanceled = false;
        var source = new Source(async (_, token) =>
        {
            using var registration = token.Register(() => remoteCanceled = true);
            entered.TrySetResult();
            return await reply.Task.WaitAsync(token);
        });
        var service = new WorkshopService(source);
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = Collect(service.ObserveDetailAsync("123", cancellationToken: firstCancellation.Token));
        await entered.Task.WaitAsync(Token);
        var second = Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(remoteCanceled);
        reply.SetResult(Item(description: "共享正文"));
        Assert.Equal("共享正文", (await second)[^1].Item!.Description);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task CancelingAllReadersPreventsLateDataFromEnteringTheCache()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<WorkshopItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new Source(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                return await reply.Task; // An uncooperative source finishes late.
            }
            return Item(description: "第二次请求");
        });
        var cache = new MemoryWorkshopCacheStore();
        var service = new WorkshopService(source, cache: cache);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = Collect(service.ObserveDetailAsync("123", cancellationToken: cancellation.Token));
        await entered.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        reply.SetResult(Item(description: "不应回填"));
        var next = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.Equal("第二次请求", next[^1].Item!.Description);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task KnownUnavailableItemsCannotBeRestoredByAnOldPage()
    {
        var cache = new MemoryWorkshopCacheStore();
        var service = new WorkshopService(new Source((id, _) => throw new WorkshopItemUnavailableException(id, "物品不可用")),
            new Browser(request => Page(request, Item())), cache: cache);
        var detail = await Collect(service.ObserveDetailAsync("123", cancellationToken: Token));
        Assert.True(detail[^1].IsUnavailable);
        var pages = await Collect(service.ObserveSearchAsync(new(), cancellationToken: Token));
        Assert.Empty(pages[^1].Page!.Items);
    }

    [Fact]
    public async Task GamesNeverShareSearchKeysOrAcceptAnotherGamesMetadata()
    {
        var request = new WorkshopSearchRequest("地图");
        Assert.NotEqual(WorkshopCachePolicy.SearchKey(request, GameCatalog.Ck3.AppId),
            WorkshopCachePolicy.SearchKey(request, GameCatalog.Hoi4.AppId));
        var service = new WorkshopService(new Source((_, _) => Task.FromResult(Item())), game: GameCatalog.Hoi4);
        var update = Assert.Single(await Collect(service.ObserveDetailAsync("123", cancellationToken: Token)));
        Assert.Null(update.Item);
        Assert.NotNull(update.Error);
    }

    private static WorkshopItem Item(string description = "说明") => new(123, Ck3.AppId, "Mod", description, null,
        new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=123"), DateTimeOffset.UnixEpoch, 1, ["1.20"], null);
    private static WorkshopBrowsePage Page(WorkshopSearchRequest request, WorkshopItem item) => new([item], request.Page, 3, 1, [], ["1.20"]);
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var update in stream) result.Add(update);
        return result;
    }
    private sealed class Source(Func<ulong, CancellationToken, Task<WorkshopItem>> fetch) : IWorkshopSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<WorkshopItem> GetItemAsync(ulong id, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _calls); return fetch(id, cancellationToken); }
    }
    private sealed class Browser(Func<WorkshopSearchRequest, WorkshopBrowsePage> fetch) : IWorkshopBrowser
    {
        public int Calls { get; private set; }
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(fetch(request)); }
    }
    private sealed class PageOnlyCache(IWorkshopCacheStore inner) : IWorkshopCacheStore
    {
        public Task<CachedWorkshopPage?> ReadPageAsync(string key, CancellationToken token = default) => inner.ReadPageAsync(key, token);
        public Task WritePageAsync(string key, CachedWorkshopPage page, CancellationToken token = default) => inner.WritePageAsync(key, page, token);
        public Task<CachedWorkshopItem?> ReadItemAsync(ulong id, CancellationToken token = default) => throw new InvalidOperationException("搜索读取了详情");
        public Task WriteItemAsync(CachedWorkshopItem item, CancellationToken token = default) => throw new InvalidOperationException("搜索写入了详情");
        public Task RemoveItemAsync(ulong id, CancellationToken token = default) => throw new InvalidOperationException("搜索移除了详情");
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
