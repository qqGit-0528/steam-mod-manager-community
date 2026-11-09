using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>可丢弃的工坊对象缓存。设置、配置清单和游戏文件不放入此目录。</summary>
public sealed class FileWorkshopCacheStore : IWorkshopCacheStore
{
    private const int SchemaVersion = 1;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly uint _appId;
    private readonly SemaphoreSlim _gate;
    private readonly object _memoryLock = new();
    private readonly Dictionary<string, MemoryEntry<CachedWorkshopPage>> _pages = [];
    private readonly Dictionary<string, MemoryEntry<CachedWorkshopItem>> _items = [];
    private long _access;
    private DateTimeOffset? _lastExpirySweep;
    private readonly object _maintenanceLock = new();
    private Task? _startupMaintenance;

    private sealed record Envelope<T>(int SchemaVersion, uint AppId, string Key, T Data);
    private readonly record struct Stamp(long Length, long Written, long Created);
    private sealed record MemoryEntry<T>(T Value, Stamp? Stamp, long Access);

    public FileWorkshopCacheStore(string dataDirectory, TimeProvider? timeProvider = null, GameDefinition? game = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _root = Path.GetFullPath(Path.Combine(dataDirectory, "cache", "v1"));
        _clock = timeProvider ?? TimeProvider.System;
        _appId = (game ?? GameCatalog.Ck3).AppId;
        _gate = Gates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CachedWorkshopPage?> ReadPageAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) return null;
        await EnsureTrimmedAsync(cancellationToken).ConfigureAwait(false);
        var path = FilePath("pages", key);
        var cached = ReadMemory(_pages, key, path);
        if (cached is not null && Valid(cached))
        {
            Touch(path);
            Remember(_pages, key, cached, path, WorkshopCacheLimits.MemoryPages);
            return cached;
        }
        var value = await ReadFileAsync<CachedWorkshopPage>(path, key, cancellationToken).ConfigureAwait(false);
        if (value is null || !Valid(value)) return null;
        Touch(path);
        Remember(_pages, key, value, path, WorkshopCacheLimits.MemoryPages);
        return value;
    }

    public async Task WritePageAsync(string key, CachedWorkshopPage page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key) || !Valid(page)) return;
        var path = FilePath("pages", key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var memory = ReadMemory(_pages, key, path);
            var selected = memory is not null && Valid(memory) && memory.FetchedAt > page.FetchedAt ? memory : page;
            try
            {
                using var diskLock = await LockFilesAsync(cancellationToken).ConfigureAwait(false);
                var previous = await ReadFileAsync<CachedWorkshopPage>(path, key, cancellationToken).ConfigureAwait(false);
                if (previous is not null && Valid(previous) && previous.FetchedAt > selected.FetchedAt) selected = previous;
                await WriteFileAsync(path, key, selected, cancellationToken).ConfigureAwait(false);
                await PruneAsync(cancellationToken, sweepExpiry: false).ConfigureAwait(false);
            }
            catch (Exception error) when (IsStorageFailure(error)) { }
            Remember(_pages, key, selected, path, WorkshopCacheLimits.MemoryPages);
        }
        finally { _gate.Release(); }
    }

    public async Task<CachedWorkshopItem?> ReadItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publishedFileId == 0) return null;
        await EnsureTrimmedAsync(cancellationToken).ConfigureAwait(false);
        var key = ItemKey(publishedFileId);
        var path = FilePath("items", key);
        var cached = ReadMemory(_items, key, path);
        if (cached is not null && Valid(cached, publishedFileId))
        {
            Touch(path);
            Remember(_items, key, cached, path, WorkshopCacheLimits.MemoryItems);
            return cached;
        }
        var value = await ReadFileAsync<CachedWorkshopItem>(path, key, cancellationToken).ConfigureAwait(false);
        if (value is null || !Valid(value, publishedFileId)) return null;
        Touch(path);
        Remember(_items, key, value, path, WorkshopCacheLimits.MemoryItems);
        return value;
    }

    public async Task WriteItemAsync(CachedWorkshopItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item?.Item is null || !Valid(item, item.Item.PublishedFileId)) return;
        var key = ItemKey(item.Item.PublishedFileId);
        var path = FilePath("items", key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousMemory = ReadMemory(_items, key, path);
            var selected = previousMemory is not null && Valid(previousMemory, item.Item.PublishedFileId)
                ? WorkshopCachePolicy.Prefer(previousMemory, item) : item;
            try
            {
                using var diskLock = await LockFilesAsync(cancellationToken).ConfigureAwait(false);
                // 在写锁内重读磁盘，避免多个实例用各自的旧 L1 覆盖较新资料。
                var previous = await ReadFileAsync<CachedWorkshopItem>(path, key, cancellationToken).ConfigureAwait(false);
                if (previous is not null && Valid(previous, item.Item.PublishedFileId))
                    selected = WorkshopCachePolicy.Prefer(previous, selected);
                await WriteFileAsync(path, key, selected, cancellationToken).ConfigureAwait(false);
                await PruneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsStorageFailure(error)) { }
            Remember(_items, key, selected, path, WorkshopCacheLimits.MemoryItems);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ItemKey(publishedFileId);
        lock (_memoryLock) _items.Remove(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using var diskLock = await LockFilesAsync(cancellationToken).ConfigureAwait(false);
                File.Delete(FilePath("items", key));
            }
            catch (Exception error) when (IsStorageFailure(error)) { }
        }
        finally { _gate.Release(); }
    }

    private string ItemKey(ulong id) => $"steam/{_appId.ToString(CultureInfo.InvariantCulture)}/{id.ToString(CultureInfo.InvariantCulture)}";
    private string FilePath(string kind, string key) => Path.Combine(_root, kind,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{_appId}/{key}"))).ToLowerInvariant() + ".json");

    private bool Valid(CachedWorkshopPage? value) => value?.Page is { } page &&
        WorkshopCachePolicy.CanDisplay(value.FetchedAt, _clock.GetUtcNow()) && page.Page >= 1 &&
        page.TotalPages >= 1 && page.TotalCount >= 0 && page.Items is not null &&
        page.CategoryTags is not null && page.VersionTags is not null &&
        page.CategoryTags.All(tag => tag is not null) && page.VersionTags.All(tag => tag is not null) &&
        page.Items.All(item => ValidItem(item));

    private bool Valid(CachedWorkshopItem? value, ulong id) => value?.Item is { } item && id > 0 &&
        WorkshopCachePolicy.CanDisplay(value.FetchedAt, _clock.GetUtcNow()) &&
        item.PublishedFileId == id && ValidItem(item);

    private bool ValidItem(WorkshopItem? item) => item is not null && item.PublishedFileId > 0 &&
        item.ConsumerAppId == _appId && item.Title is not null && item.Description is not null &&
        item.Tags is not null && item.Tags.All(tag => tag is not null) && item.FileSizeBytes >= 0 &&
        item.WorkshopUrl is { IsAbsoluteUri: true };

    private T? ReadMemory<T>(Dictionary<string, MemoryEntry<T>> values, string key, string path) where T : class
    {
        lock (_memoryLock)
        {
            if (!values.TryGetValue(key, out var entry)) return null;
            var currentStamp = FileStamp(path);
            if (entry.Stamp != currentStamp)
            {
                values.Remove(key);
                return null;
            }
            values[key] = entry with { Access = ++_access };
            return entry.Value;
        }
    }

    private void Remember<T>(Dictionary<string, MemoryEntry<T>> values, string key, T value, string path, int capacity)
    {
        var stamp = FileStamp(path);
        lock (_memoryLock)
        {
            values[key] = new MemoryEntry<T>(value, stamp, ++_access);
            while (values.Count > capacity) values.Remove(values.MinBy(pair => pair.Value.Access).Key);
        }
    }

    private static Stamp? FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
        }
        catch (Exception error) when (IsStorageFailure(error)) { return null; }
    }

    private async Task<T?> ReadFileAsync<T>(string path, string key, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            var file = JsonSerializer.Deserialize<Envelope<T>>(bytes, Json);
            return file is not null && file.SchemaVersion == SchemaVersion && file.AppId == _appId && file.Key == key
                ? file.Data : null;
        }
        catch (Exception error) when (IsStorageFailure(error) || error is JsonException or NotSupportedException) { return null; }
    }

    private static async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if (length > MaximumFileBytes) return null;
        var bytes = new byte[(int)length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return input.Length == bytes.Length ? bytes : null;
    }

    private async Task WriteFileAsync<T>(string path, string key, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new LimitedBuffer(MaximumFileBytes);
        await JsonSerializer.SerializeAsync(buffer, new Envelope<T>(SchemaVersion, _appId, key, value), Json, cancellationToken).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            Touch(path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (IsStorageFailure(error)) { }
        }
    }

    private async Task<FileStream> LockFilesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var until = Environment.TickCount64 + 2000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(_root, ".objects.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < until)
            { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
    }

    private Task EnsureTrimmedAsync(CancellationToken token)
    {
        Task maintenance;
        // 首读先落实容量上限。目标文件仍逐条验证，过期清扫由写入或显式维护触发。
        lock (_maintenanceLock) maintenance = _startupMaintenance ??= Task.Run(() => TrimCoreAsync(false, default));
        return maintenance.WaitAsync(token);
    }

    /// <summary>清理可再获取的浏览缓存，不读取到内存或改动设置、配置、Mod 内容。</summary>
    public Task TrimAsync(CancellationToken cancellationToken = default) => TrimCoreAsync(true, cancellationToken);

    private async Task TrimCoreAsync(bool sweepExpiry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var diskLock = await LockFilesAsync(cancellationToken).ConfigureAwait(false);
            await PruneAsync(cancellationToken, sweepExpiry).ConfigureAwait(false);
        }
        catch (Exception error) when (IsStorageFailure(error)) { }
        finally { _gate.Release(); }
    }

    private async Task PruneAsync(CancellationToken cancellationToken, bool sweepExpiry = true)
    {
        var now = _clock.GetUtcNow();
        var checkExpiry = sweepExpiry && (_lastExpirySweep is null || now - _lastExpirySweep.Value >= TimeSpan.FromMinutes(1));
        var pages = new List<FileInfo>();
        var items = new List<FileInfo>();
        foreach (var (kind, files) in new[] { ("pages", pages), ("items", items) })
        {
            var folder = Path.Combine(_root, kind);
            if (!Directory.Exists(folder)) continue;
            // 已持有跨进程对象写锁，这些是上次进程中断遗留的可丢弃缓存临时文件。
            foreach (var temporary in Directory.EnumerateFiles(folder, "*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(temporary);
            }
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (!checkExpiry && info.Length <= MaximumFileBytes)
                {
                    files.Add(info);
                    continue;
                }
                var keep = false;
                if (info.Length <= MaximumFileBytes)
                {
                    try
                    {
                        var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                        if (bytes is not null)
                        {
                            using var document = JsonDocument.Parse(bytes);
                            var root = document.RootElement;
                            keep = root.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var schema) && schema == SchemaVersion &&
                                root.TryGetProperty("appId", out var app) && app.TryGetUInt32(out var appId) && appId > 0 &&
                                root.TryGetProperty("data", out var data) && data.TryGetProperty("fetchedAt", out var fetched) &&
                                fetched.TryGetDateTimeOffset(out var date) && WorkshopCachePolicy.CanDisplay(date, _clock.GetUtcNow());
                        }
                    }
                    catch (Exception error) when (IsStorageFailure(error) || error is JsonException or InvalidOperationException) { }
                }
                if (!keep) File.Delete(path);
                else files.Add(info);
            }
        }
        var total = pages.Sum(file => file.Length) + items.Sum(file => file.Length);
        foreach (var (files, limit) in new[] { (pages, WorkshopCacheLimits.DiskPages), (items, WorkshopCacheLimits.DiskItems) })
        {
            if (files.Count <= limit) continue;
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).Take(files.Count - limit).ToArray())
            {
                total -= file.Length;
                file.Delete();
                files.Remove(file);
            }
        }
        if (total > WorkshopCacheLimits.MetadataDiskBytes)
        {
            foreach (var file in pages.Concat(items).OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= WorkshopCacheLimits.MetadataDiskBytes) break;
                total -= file.Length;
                file.Delete();
            }
        }
        if (checkExpiry) _lastExpirySweep = now;
    }

    private void Touch(string path)
    {
        // OS 的 atime 会被后台校验读取污染。使用可控写入时间记录使用顺序；
        // 新鲜度始终读取 JSON 中的 FetchedAt，不读取这个文件时间。
        try { File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime); }
        catch (Exception error) when (IsStorageFailure(error)) { }
    }

    private static bool IsStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private sealed class LimitedBuffer(int limit) : MemoryStream
    {
        private void Check(int count)
        {
            if (Length + count > limit) throw new IOException("工坊缓存条目超过大小限制。");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    }
}
