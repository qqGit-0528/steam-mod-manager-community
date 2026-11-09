using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

/// <summary>供无文件存储的调用方使用；每个服务／游戏独立，有界且不延长采集时间。</summary>
public sealed class MemoryWorkshopCacheStore(GameDefinition? game = null) : IWorkshopCacheStore
{
    private readonly uint _appId = (game ?? GameCatalog.Ck3).AppId;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedWorkshopPage> _pages = [];
    private readonly Dictionary<ulong, CachedWorkshopItem> _items = [];

    public Task<CachedWorkshopPage?> ReadPageAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_pages.GetValueOrDefault(key));
    }

    public Task WritePageAsync(string key, CachedWorkshopPage page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.Page.Items.Any(item => item.ConsumerAppId != _appId)) throw new ArgumentException("缓存游戏不匹配。");
        lock (_gate)
        {
            if (!_pages.TryGetValue(key, out var current) || current.FetchedAt <= page.FetchedAt) _pages[key] = page;
            if (_pages.Count > WorkshopCacheLimits.MemoryPages) _pages.Remove(_pages.MinBy(pair => pair.Value.FetchedAt).Key);
        }
        return Task.CompletedTask;
    }

    public Task<CachedWorkshopItem?> ReadItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_items.GetValueOrDefault(publishedFileId));
    }

    public Task WriteItemAsync(CachedWorkshopItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Item.ConsumerAppId != _appId || item.Item.PublishedFileId == 0) throw new ArgumentException("缓存条目不匹配。");
        lock (_gate)
        {
            var id = item.Item.PublishedFileId;
            _items[id] = _items.TryGetValue(id, out var current) ? WorkshopCachePolicy.Prefer(current, item) : item;
            if (_items.Count > WorkshopCacheLimits.MemoryItems) _items.Remove(_items.MinBy(pair => pair.Value.FetchedAt).Key);
        }
        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items.Remove(publishedFileId);
        return Task.CompletedTask;
    }
}
