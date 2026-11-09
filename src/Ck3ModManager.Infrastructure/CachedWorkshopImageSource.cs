using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>共享图片字节及可恢复磁盘缓存；Bitmap 的解码和释放仍由调用方管理。</summary>
public sealed class CachedWorkshopImageSource : IWorkshopImageSource, IWorkshopPreviewUpdates
{
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumIndexBytes = 64 * 1024;
    private static readonly TimeSpan MaximumFreshness = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumStaleness = TimeSpan.FromDays(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SteamWorkshopImageSource _source;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _freshDuration;
    private readonly long _maximumDiskBytes;
    private readonly int _maximumMemoryBytes;
    private readonly object _memoryLock = new();
    private readonly Dictionary<string, (PreviewEntry Entry, long Used)> _memory = new(StringComparer.Ordinal);
    private long _memoryBytes, _sequence;
    private readonly object _flightLock = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly object _maintenanceLock = new();
    private Task? _startupMaintenance;

    public CachedWorkshopImageSource(SteamWorkshopImageSource source, string dataDirectory,
        TimeProvider? timeProvider = null, TimeSpan? freshDuration = null,
        long maximumDiskBytes = WorkshopCacheLimits.PreviewDiskBytes, int maximumMemoryBytes = WorkshopCacheLimits.PreviewMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _directory = Path.GetFullPath(Path.Combine(dataDirectory, "cache", "v1", "previews"));
        _clock = timeProvider ?? TimeProvider.System;
        _freshDuration = freshDuration ?? MaximumFreshness;
        if (_freshDuration < TimeSpan.Zero || _freshDuration > MaximumFreshness) throw new ArgumentOutOfRangeException(nameof(freshDuration));
        if (maximumDiskBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumDiskBytes));
        if (maximumMemoryBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumMemoryBytes));
        _maximumDiskBytes = maximumDiskBytes;
        _maximumMemoryBytes = maximumMemoryBytes;
    }

    public async Task<byte[]> GetPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        WorkshopPreviewUpdate? result = null;
        string? error = null;
        await foreach (var update in ObservePreviewAsync(url, cancellationToken).ConfigureAwait(false))
        {
            if (update.HasBytes) result = update;
            error = update.Error;
        }
        return result?.Bytes ?? throw new HttpRequestException(error ?? "图片未返回正文。");
    }

    public async IAsyncEnumerable<WorkshopPreviewUpdate> ObservePreviewAsync(string url,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateUrl(url);
        await EnsureTrimmedAsync(cancellationToken).ConfigureAwait(false);
        var cached = await ReadAsync(url, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (cached is not null && !cached.Index.RevalidateAlways && cached.Index.FreshUntil > now)
        {
            yield return WorkshopPreviewUpdate.FromSharedBuffer(cached.Bytes);
            yield break;
        }
        var canShowStale = cached is not null && !cached.Index.RevalidateAlways && !cached.Index.MustRevalidate;
        if (canShowStale) yield return WorkshopPreviewUpdate.FromSharedBuffer(cached!.Bytes, isStale: true, isRefreshing: true);

        PreviewEntry? fetched = null;
        string? error = null;
        try { fetched = await FetchSharedAsync(url, cached, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { error = $"封面刷新失败：{exception.Message}"; }
        if (fetched is not null)
            yield return WorkshopPreviewUpdate.FromSharedBuffer(fetched.Bytes);
        else
            yield return WorkshopPreviewUpdate.FromSharedBuffer(canShowStale ? cached!.Bytes : null,
                isStale: canShowStale, error: error);
    }

    private async Task<PreviewEntry> FetchSharedAsync(string url, PreviewEntry? cached, CancellationToken callerToken)
    {
        Flight flight;
        lock (_flightLock)
        {
            if (!_flights.TryGetValue(url, out flight!))
            {
                flight = new Flight();
                _flights.Add(url, flight);
                flight.Task = FetchAndStoreAsync(url, cached, flight.Cancellation.Token);
            }
            flight.Waiters++;
        }
        try { return await flight.Task!.WaitAsync(callerToken).ConfigureAwait(false); }
        finally
        {
            lock (_flightLock)
            {
                if (--flight.Waiters == 0)
                {
                    if (_flights.TryGetValue(url, out var current) && ReferenceEquals(current, flight)) _flights.Remove(url);
                    if (!flight.Task!.IsCompleted) flight.Cancellation.Cancel();
                    _ = flight.Task.ContinueWith(_ => flight.Cancellation.Dispose(), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task<PreviewEntry> FetchAndStoreAsync(string url, PreviewEntry? cached, CancellationToken cancellationToken)
    {
        var result = await _source.FetchAsync(url, cached?.Index.EntityTag, cached?.Index.LastModified, cancellationToken).ConfigureAwait(false);
        if (result.NotModified && cached is null)
            throw new HttpRequestException("Steam 返回 304，但本地没有可用的图片正文。");
        var bytes = result.NotModified ? cached!.Bytes : result.Bytes!;
        if (bytes.Length == 0) throw new HttpRequestException("Steam 图片正文为空。");
        var now = _clock.GetUtcNow();
        var previous = result.NotModified ? cached?.Index : null;
        var headers = result.Headers;
        var noStore = headers.NoStore ?? previous?.NoStore ?? false;
        var revalidate = headers.NoCache ?? previous?.RevalidateAlways ?? false;
        var mustRevalidate = headers.MustRevalidate ?? previous?.MustRevalidate ?? false;
        var lifetime = _freshDuration;
        if (headers.MaxAge is { } maxAge)
        {
            var apparentAge = headers.Date is { } date && now > date ? now - date : TimeSpan.Zero;
            var age = headers.Age is { } ageHeader && ageHeader > apparentAge ? ageHeader : apparentAge;
            lifetime = maxAge - age;
        }
        else if (headers.Expires is { } expires) lifetime = expires - now;
        else if (previous is not null) lifetime = TimeSpan.FromTicks(previous.LifetimeTicks);
        lifetime = TimeSpan.FromTicks(Math.Clamp(lifetime.Ticks, 0, _freshDuration.Ticks));
        if (revalidate) lifetime = TimeSpan.Zero;
        var index = new PreviewIndex(1, url, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, now,
            now + lifetime, now + MaximumStaleness, lifetime.Ticks, noStore, revalidate, mustRevalidate,
            headers.EntityTag ?? previous?.EntityTag, headers.LastModified ?? previous?.LastModified);
        var entry = new PreviewEntry(bytes, index);
        cancellationToken.ThrowIfCancellationRequested();
        if (noStore) await RemoveAsync(url, cancellationToken).ConfigureAwait(false);
        else await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private async Task<PreviewEntry?> ReadAsync(string url, CancellationToken cancellationToken)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(url, out var memory))
            {
                if (Usable(memory.Entry.Index, url))
                {
                    _memory[url] = (memory.Entry, ++_sequence);
                    return memory.Entry;
                }
                RemoveMemory(url);
            }
        }
        try
        {
            var indexPath = IndexPath(url);
            var bytes = await ReadLimitedAsync(indexPath, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            var index = JsonSerializer.Deserialize<PreviewIndex>(bytes, JsonOptions);
            if (index is null || !Usable(index, url)) return null;
            var body = await ReadLimitedAsync(BlobPath(index), MaximumImageBytes, cancellationToken).ConfigureAwait(false);
            if (body is null || body.Length != index.Length || Convert.ToHexString(SHA256.HashData(body)) != index.ContentHash) return null;
            var entry = new PreviewEntry(body, index);
            Remember(entry);
            TryTouch(indexPath);
            return entry;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsStorageError(exception)) { return null; }
    }

    private bool Usable(PreviewIndex index, string url)
    {
        var now = _clock.GetUtcNow();
        return index.Schema == 1 && index.Url == url && !index.NoStore && index.Length > 0 && index.Length <= MaximumImageBytes &&
            index.ContentHash is { Length: 64 } && index.ContentHash.All(char.IsAsciiHexDigit) &&
            index.FetchedAt <= now && index.StaleUntil > now && index.StaleUntil <= index.FetchedAt + MaximumStaleness &&
            index.FreshUntil >= index.FetchedAt && index.FreshUntil <= index.FetchedAt + MaximumFreshness &&
            index.LifetimeTicks >= 0 && index.LifetimeTicks <= MaximumFreshness.Ticks;
    }

    private async Task SaveAsync(PreviewEntry entry, CancellationToken cancellationToken)
    {
        Remember(entry);
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(entry.Index, JsonOptions);
        if (indexBytes.Length > MaximumIndexBytes || entry.Bytes.Length + indexBytes.Length > _maximumDiskBytes || _maximumDiskBytes == 0) return;
        var gate = DirectoryLocks.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            await using var processLock = await TryLockAsync(cancellationToken).ConfigureAwait(false);
            if (processLock is null) return;
            await WriteAtomicAsync(BlobPath(entry.Index), entry.Bytes, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(IndexPath(entry.Index.Url), indexBytes, cancellationToken).ConfigureAwait(false);
            TryTouch(IndexPath(entry.Index.Url));
            CleanupDisk();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsStorageError(exception)) { /* A successful download remains usable if cache storage fails. */ }
        finally { gate.Release(); }
    }

    private void Remember(PreviewEntry entry)
    {
        lock (_memoryLock)
        {
            RemoveMemory(entry.Index.Url);
            if (entry.Index.NoStore || entry.Bytes.Length > _maximumMemoryBytes) return;
            while (_memoryBytes + entry.Bytes.Length > _maximumMemoryBytes && _memory.Count > 0)
                RemoveMemory(_memory.MinBy(item => item.Value.Used).Key);
            _memory[entry.Index.Url] = (entry, ++_sequence);
            _memoryBytes += entry.Bytes.Length;
        }
    }

    private void RemoveMemory(string url)
    {
        if (_memory.Remove(url, out var previous)) _memoryBytes -= previous.Entry.Bytes.Length;
    }

    private async Task RemoveAsync(string url, CancellationToken cancellationToken)
    {
        lock (_memoryLock) RemoveMemory(url);
        var gate = DirectoryLocks.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return;
            await using var processLock = await TryLockAsync(cancellationToken).ConfigureAwait(false);
            if (processLock is null) return;
            File.Delete(IndexPath(url));
            foreach (var file in Directory.EnumerateFiles(_directory, Hash(url) + ".*.bin")) File.Delete(file);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsStorageError(exception)) { }
        finally { gate.Release(); }
    }

    private Task EnsureTrimmedAsync(CancellationToken token)
    {
        Task maintenance;
        lock (_maintenanceLock) maintenance = _startupMaintenance ??= Task.Run(() => TrimAsync());
        return maintenance.WaitAsync(token);
    }

    public async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_directory)) return;
        var gate = DirectoryLocks.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await TryLockAsync(cancellationToken).ConfigureAwait(false);
            if (processLock is not null) CleanupDisk();
        }
        catch (Exception error) when (IsStorageError(error)) { }
        finally { gate.Release(); }
    }

    private void CleanupDisk()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.tmp")) File.Delete(file);
        var indexes = Directory.EnumerateFiles(_directory, "*.json").Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        var knownBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in indexes.ToArray())
        {
            PreviewIndex? index = null;
            if (file.Length <= MaximumIndexBytes)
                try { index = JsonSerializer.Deserialize<PreviewIndex>(File.ReadAllBytes(file.FullName), JsonOptions); }
                catch (Exception exception) when (IsStorageError(exception)) { }
            if (index is null || !Uri.TryCreate(index.Url, UriKind.Absolute, out var uri) ||
                !SteamWorkshopImageSource.IsAllowedUrl(uri) || file.Name != Hash(index.Url) + ".json" || !Usable(index, index.Url))
            {
                file.Delete();
                indexes.Remove(file);
            }
            else knownBlobs.Add(BlobPath(index));
        }
        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
            if (!knownBlobs.Contains(file)) File.Delete(file);
        long size = Directory.EnumerateFiles(_directory).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Sum(path => new FileInfo(path).Length);
        foreach (var index in indexes)
        {
            if (size <= _maximumDiskBytes) break;
            var blobPrefix = Path.GetFileNameWithoutExtension(index.Name) + ".";
            size -= index.Length;
            index.Delete();
            foreach (var blob in Directory.EnumerateFiles(_directory, blobPrefix + "*.bin"))
            {
                var info = new FileInfo(blob);
                size -= info.Length;
                info.Delete();
            }
        }
    }

    private async Task<FileStream?> TryLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(_directory, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
        return null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > limit) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) return null;
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (IsStorageError(exception)) { }
        }
    }

    private void TryTouch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime); }
        catch (Exception exception) when (IsStorageError(exception)) { }
    }

    private string IndexPath(string url) => Path.Combine(_directory, Hash(url) + ".json");
    private string BlobPath(PreviewIndex index) => Path.Combine(_directory, Hash(index.Url) + "." + index.ContentHash + ".bin");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException;
    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SteamWorkshopImageSource.IsAllowedUrl(uri))
            throw new InvalidOperationException("封面地址不是受支持的 Steam 图片来源。");
    }

    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<PreviewEntry>? Task;
        internal int Waiters;
    }

    private sealed record PreviewEntry(byte[] Bytes, PreviewIndex Index);
    private sealed record PreviewIndex(int Schema, string Url, string ContentHash, int Length, DateTimeOffset FetchedAt,
        DateTimeOffset FreshUntil, DateTimeOffset StaleUntil, long LifetimeTicks, bool NoStore, bool RevalidateAlways,
        bool MustRevalidate, string? EntityTag, DateTimeOffset? LastModified);
}
