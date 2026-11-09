using System.Globalization;

namespace Ck3ModManager.Core;

/// <summary>浏览缓存只保留近期访问内容；不用于保存已下载的 Mod 文件。</summary>
public static class WorkshopCacheLimits
{
    public const int MemoryPages = 4;
    public const int MemoryItems = 32;
    public const int DiskPages = 16;
    public const int DiskItems = 64;
    public const long MetadataDiskBytes = 4L * 1024 * 1024;
    public const long PreviewDiskBytes = 32L * 1024 * 1024;
    public const int PreviewMemoryBytes = 8 * 1024 * 1024;
}

public sealed record CachedWorkshopPage(WorkshopBrowsePage Page, DateTimeOffset FetchedAt);

/// <summary>完整度只表示是否已采集详情正文，不表示依赖或历史文件已齐全。</summary>
public sealed record CachedWorkshopItem(WorkshopItem Item, DateTimeOffset FetchedAt, bool HasFullDescription);

public interface IWorkshopCacheStore
{
    Task<CachedWorkshopPage?> ReadPageAsync(string key, CancellationToken cancellationToken = default);
    Task WritePageAsync(string key, CachedWorkshopPage page, CancellationToken cancellationToken = default);
    Task<CachedWorkshopItem?> ReadItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default);
    Task WriteItemAsync(CachedWorkshopItem item, CancellationToken cancellationToken = default);
    Task RemoveItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default);
}

public sealed record WorkshopSearchUpdate(WorkshopBrowsePage? Page, DateTimeOffset? FetchedAt = null,
    bool IsStale = false, bool IsRefreshing = false, string? Error = null);

public sealed record WorkshopDetailUpdate(WorkshopItem? Item, bool HasFullDescription = false,
    DateTimeOffset? FetchedAt = null, bool IsStale = false, bool IsRefreshing = false,
    string? Error = null, bool IsUnavailable = false);

public sealed record WorkshopPreviewUpdate(byte[]? Bytes, bool IsStale = false,
    bool IsRefreshing = false, string? Error = null)
{
    private readonly byte[]? _bytes = Bytes;
    private bool Shared { get; init; }

    public byte[]? Bytes { get => Shared ? _bytes?.ToArray() : _bytes; init => _bytes = value; }
    public bool HasBytes => _bytes is not null;
    public Stream? OpenRead() => _bytes is { Length: > 0 } ? new MemoryStream(_bytes, writable: false) : null;

    /// <summary>来源必须保持缓冲区只读。调用方可打开只读流，或取得独立数组。</summary>
    public static WorkshopPreviewUpdate FromSharedBuffer(byte[]? bytes, bool isStale = false,
        bool isRefreshing = false, string? error = null) => new(bytes, isStale, isRefreshing, error) { Shared = true };
}

public interface IWorkshopPreviewUpdates
{
    IAsyncEnumerable<WorkshopPreviewUpdate> ObservePreviewAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>相同修订优先完整详情；列表观察不能延长详情的采集时间。</summary>
public static class WorkshopCachePolicy
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    public static bool IsFresh(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        fetchedAt <= now && now - fetchedAt < FreshFor;

    public static bool CanDisplay(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        fetchedAt <= now && now - fetchedAt < KeepFor;

    public static CachedWorkshopItem Prefer(CachedWorkshopItem current, CachedWorkshopItem incoming)
    {
        if (current.Item.PublishedFileId != incoming.Item.PublishedFileId ||
            current.Item.ConsumerAppId != incoming.Item.ConsumerAppId)
            throw new ArgumentException("不能合并不同工坊条目的缓存。");
        if (incoming.Item.UpdatedAt != current.Item.UpdatedAt)
            return incoming.Item.UpdatedAt > current.Item.UpdatedAt ? incoming : current;
        if (current.HasFullDescription != incoming.HasFullDescription)
            return incoming.HasFullDescription ? incoming : current;
        return incoming.FetchedAt >= current.FetchedAt ? incoming : current;
    }

    public static string SearchKey(WorkshopSearchRequest request, uint appId = Ck3.AppId) =>
        string.Join("/", "steam", appId.ToString(CultureInfo.InvariantCulture), "schinese", "alldays",
            ((int)request.Sort).ToString(CultureInfo.InvariantCulture), request.Page.ToString(CultureInfo.InvariantCulture),
            Uri.EscapeDataString(request.SearchText.Trim()), Uri.EscapeDataString(request.CategoryTag ?? ""),
            Uri.EscapeDataString(request.VersionTag ?? ""));
}

public sealed class WorkshopItemUnavailableException(ulong publishedFileId, string message) : Exception(message)
{
    public ulong PublishedFileId { get; } = publishedFileId;
}
