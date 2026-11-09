using static Ck3ModManager.Core.AppText;
using System.Runtime.CompilerServices;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed class WorkshopService
{
    private readonly IWorkshopSource _source;
    private readonly IWorkshopBrowser? _browser;
    private readonly IWorkshopImageSource? _images;
    private readonly IWorkshopCacheStore _cache;
    private readonly bool _persistentCacheConfigured;
    private readonly TimeProvider _clock;
    private readonly SharedWorkshopRequests _requests = new();
    private readonly object _unavailableLock = new();
    private readonly Dictionary<ulong, DateTimeOffset> _unavailable = [];

    public WorkshopService(IWorkshopSource source, IWorkshopBrowser? browser = null,
        IWorkshopImageSource? images = null, GameDefinition? game = null,
        IWorkshopCacheStore? cache = null, TimeProvider? timeProvider = null)
    {
        _source = source;
        _browser = browser;
        _images = images;
        Game = game ?? GameCatalog.Ck3;
        _clock = timeProvider ?? TimeProvider.System;
        _cache = cache ?? new MemoryWorkshopCacheStore(Game);
        _persistentCacheConfigured = cache is not null;
    }

    public GameDefinition Game { get; }
    public bool CanBrowse => _browser is not null;

    // Existing single-result adapters retain their direct-source contract without an injected cache.
    public async Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (!_persistentCacheConfigured)
            return await FetchPageAsync(request, cancellationToken).ConfigureAwait(false);
        WorkshopBrowsePage? result = null;
        await foreach (var update in ObserveSearchAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (update.Error is not null) throw new InvalidOperationException(update.Error);
            if (update.Page is not null) result = update.Page;
        }
        return result ?? throw new InvalidOperationException(T("CouldNotReadTheWorkshopList"));
    }

    public async Task<WorkshopItem> LookupAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = WorkshopReference.Parse(reference).PublishedFileId;
        if (!_persistentCacheConfigured)
            return await FetchItemAsync(id, cancellationToken).ConfigureAwait(false);
        WorkshopItem? result = null;
        await foreach (var update in ObserveDetailAsync(reference, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (update.Error is not null) throw new InvalidOperationException(update.Error);
            if (update.Item is not null && update.HasFullDescription && !update.IsStale) result = update.Item;
        }
        return result ?? throw new InvalidOperationException(T("CouldNotReadFullWorkshopDetails"));
    }

    public Task<byte[]> GetPreviewAsync(string url, CancellationToken cancellationToken = default) =>
        _images?.GetPreviewAsync(url, cancellationToken) ?? Task.FromResult(Array.Empty<byte>());

    public async IAsyncEnumerable<WorkshopPreviewUpdate> ObservePreviewAsync(string url,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_images is IWorkshopPreviewUpdates previews)
        {
            await foreach (var update in previews.ObservePreviewAsync(url, cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        else yield return new WorkshopPreviewUpdate(await GetPreviewAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<WorkshopSearchUpdate> ObserveSearchAsync(WorkshopSearchRequest request,
        bool forceRefresh = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_browser is null) throw new InvalidOperationException(T("NoWorkshopBrowsingSourceIsConfigured"));
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request), T("PageNumberMustBeGreaterThanZero"));
        var key = WorkshopCachePolicy.SearchKey(request, Game.AppId);
        var cached = await TryReadPageAsync(key, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (cached is not null && IsValidPage(cached.Page, request.Page) && WorkshopCachePolicy.CanDisplay(cached.FetchedAt, now))
        {
            var fresh = WorkshopCachePolicy.IsFresh(cached.FetchedAt, now);
            yield return new WorkshopSearchUpdate(FilterUnavailable(cached.Page),
                cached.FetchedAt, !fresh, forceRefresh || !fresh);
            if (fresh && !forceRefresh) yield break;
        }
        else cached = null;

        CachedWorkshopPage? fetched = null;
        Exception? failure = null;
        try
        {
            fetched = await _requests.RunAsync("page/" + key, async token =>
            {
                var page = await FetchPageAsync(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var entry = new CachedWorkshopPage(page, _clock.GetUtcNow());
                await TryWritePageAsync(key, entry, token).ConfigureAwait(false);
                return entry;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { failure = error; }
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            yield return new WorkshopSearchUpdate(cached is null ? null : FilterUnavailable(cached.Page),
                cached?.FetchedAt, cached is not null, Error: T("CouldNotRefreshTheWorkshopList", failure.Message));
            yield break;
        }
        yield return new WorkshopSearchUpdate(FilterUnavailable(fetched!.Page), fetched.FetchedAt);
    }

    public async IAsyncEnumerable<WorkshopDetailUpdate> ObserveDetailAsync(string reference, WorkshopItem? existing = null,
        bool forceRefresh = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = WorkshopReference.Parse(reference).PublishedFileId;
        if (existing is not null) ValidateItem(existing, id);
        if (IsUnavailable(id))
        {
            if (!forceRefresh)
            {
                yield return new WorkshopDetailUpdate(null, Error: T("ThisWorkshopItemIsUnavailableTryRefreshing"), IsUnavailable: true);
                yield break;
            }
        }
        // The selected list item is usable before any disk or network work.
        if (existing is not null)
            yield return new WorkshopDetailUpdate(existing, IsRefreshing: true);
        var cached = await TryReadItemAsync(id, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (cached is not null && (!WorkshopCachePolicy.CanDisplay(cached.FetchedAt, now) ||
            cached.Item.ConsumerAppId != Game.AppId || cached.Item.PublishedFileId != id)) cached = null;
        if (cached is not null && existing is not null && existing.UpdatedAt > cached.Item.UpdatedAt) cached = null;
        if (cached is not null)
        {
            var fresh = cached.HasFullDescription && WorkshopCachePolicy.IsFresh(cached.FetchedAt, now);
            yield return new WorkshopDetailUpdate(MergeSubscriptions(cached.Item, existing), cached.HasFullDescription,
                cached.FetchedAt, !fresh, forceRefresh || !fresh);
            if (fresh && !forceRefresh) yield break;
        }

        CachedWorkshopItem? fetched = null;
        Exception? failure = null;
        try
        {
            fetched = await _requests.RunAsync("detail/" + Game.AppId + "/" + id, async token =>
            {
                var item = await FetchItemAsync(id, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var entry = new CachedWorkshopItem(item, _clock.GetUtcNow(), true);
                await TryWriteItemAsync(entry, token).ConfigureAwait(false);
                var stored = await TryReadItemAsync(id, token).ConfigureAwait(false);
                lock (_unavailableLock) _unavailable.Remove(id);
                return stored is null ? entry : WorkshopCachePolicy.Prefer(entry, stored);
            }, cancellationToken).ConfigureAwait(false);
            if (existing is not null && fetched.Item.UpdatedAt < existing.UpdatedAt)
                throw new InvalidOperationException(T("TheDetailsWereOlderThanTheList"));
            if (!fetched.HasFullDescription)
                throw new InvalidOperationException(T("ANewerWorkshopEntryWasFoundRefresh"));
            if (!WorkshopCachePolicy.IsFresh(fetched.FetchedAt, _clock.GetUtcNow()))
                throw new InvalidOperationException(T("TheSourceReturnedAnOlderVersionThe"));
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { failure = error; }
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is WorkshopItemUnavailableException)
        {
            lock (_unavailableLock)
            {
                _unavailable[id] = _clock.GetUtcNow() + WorkshopCachePolicy.FreshFor;
                if (_unavailable.Count > 128) _unavailable.Remove(_unavailable.MinBy(pair => pair.Value).Key);
            }
            await TryRemoveItemAsync(id, cancellationToken).ConfigureAwait(false);
            yield return new WorkshopDetailUpdate(null, Error: failure.Message, IsUnavailable: true);
            yield break;
        }
        if (failure is not null)
        {
            // 刷新等待期间可能已有更新摘要／详情写入。失败不能把首帧副本重新盖回去。
            var latest = await TryReadItemAsync(id, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Item.PublishedFileId == id && latest.Item.ConsumerAppId == Game.AppId &&
                WorkshopCachePolicy.CanDisplay(latest.FetchedAt, _clock.GetUtcNow()))
                cached = cached is null ? latest : WorkshopCachePolicy.Prefer(cached, latest);
            if (fetched is not null && fetched.Item.ConsumerAppId == Game.AppId && fetched.Item.PublishedFileId == id &&
                WorkshopCachePolicy.CanDisplay(fetched.FetchedAt, _clock.GetUtcNow()))
                cached = cached is null ? fetched : WorkshopCachePolicy.Prefer(cached, fetched);
            if (existing is not null && cached is not null && existing.UpdatedAt > cached.Item.UpdatedAt) cached = null;
            yield return new WorkshopDetailUpdate(cached is null ? existing : MergeSubscriptions(cached.Item, existing),
                cached?.HasFullDescription ?? false, cached?.FetchedAt, IsStale: true,
                Error: T("CouldNotRefreshWorkshopDetails2", failure.Message));
            yield break;
        }
        yield return new WorkshopDetailUpdate(MergeSubscriptions(fetched!.Item, existing), true, fetched.FetchedAt);
    }

    private async Task<WorkshopBrowsePage> FetchPageAsync(WorkshopSearchRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_browser is null) throw new InvalidOperationException(T("NoWorkshopBrowsingSourceIsConfigured"));
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request));
        var page = await _browser.SearchAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsValidPage(page, request.Page)) throw new InvalidOperationException(T("TheListContainsItemsOutsideOrHas", Game.ShortName));
        return page;
    }

    private bool IsValidPage(WorkshopBrowsePage page, int expectedPage) =>
        page.Page == expectedPage && page.TotalPages >= 1 && page.TotalCount >= 0 &&
        page.Items.All(item => item.ConsumerAppId == Game.AppId && item.PublishedFileId > 0);

    private async Task<WorkshopItem> FetchItemAsync(ulong id, CancellationToken token)
    {
        var item = await _source.GetItemAsync(id, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ValidateItem(item, id);
        return item;
    }

    private void ValidateItem(WorkshopItem item, ulong id)
    {
        if (item.ConsumerAppId != Game.AppId)
            throw new InvalidOperationException(T("ThisWorkshopItemDoesNotBelongTo", Game.DisplayName, Game.ShortName));
        if (item.PublishedFileId != id) throw new InvalidOperationException(T("TheWorkshopReturnedADifferentItemID"));
    }

    private WorkshopBrowsePage FilterUnavailable(WorkshopBrowsePage page)
    {
        lock (_unavailableLock)
        {
            if (_unavailable.Count == 0) return page;
            var now = _clock.GetUtcNow();
            return page with { Items = page.Items.Where(item =>
                !_unavailable.TryGetValue(item.PublishedFileId, out var until) || until <= now).ToArray() };
        }
    }

    private bool IsUnavailable(ulong id)
    {
        lock (_unavailableLock)
        {
            if (!_unavailable.TryGetValue(id, out var until)) return false;
            if (_clock.GetUtcNow() < until) return true;
            _unavailable.Remove(id);
            return false;
        }
    }

    private static WorkshopItem MergeSubscriptions(WorkshopItem item, WorkshopItem? fallback) =>
        item.Subscriptions is null && fallback?.Subscriptions is { } count ? item with { Subscriptions = count } : item;

    private static bool IsCacheError(Exception error) => error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;

    private async Task<CachedWorkshopPage?> TryReadPageAsync(string key, CancellationToken token)
    {
        try { return await _cache.ReadPageAsync(key, token).ConfigureAwait(false); }
        catch (Exception error) when (IsCacheError(error)) { return null; }
    }

    private async Task<CachedWorkshopItem?> TryReadItemAsync(ulong id, CancellationToken token)
    {
        try { return await _cache.ReadItemAsync(id, token).ConfigureAwait(false); }
        catch (Exception error) when (IsCacheError(error)) { return null; }
    }

    private async Task TryWritePageAsync(string key, CachedWorkshopPage page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await _cache.WritePageAsync(key, page, token).ConfigureAwait(false); }
        catch (Exception error) when (IsCacheError(error)) { }
    }

    private async Task TryWriteItemAsync(CachedWorkshopItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await _cache.WriteItemAsync(item, token).ConfigureAwait(false); }
        catch (Exception error) when (IsCacheError(error)) { }
    }

    private async Task TryRemoveItemAsync(ulong id, CancellationToken token)
    {
        try { await _cache.RemoveItemAsync(id, token).ConfigureAwait(false); }
        catch (Exception error) when (IsCacheError(error)) { }
    }
}
