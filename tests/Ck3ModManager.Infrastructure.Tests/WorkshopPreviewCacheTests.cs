using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopPreviewCacheTests : IDisposable
{
    private const string Url = "https://images.steamusercontent.com/cover.png";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck3-preview-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private string CacheDirectory => Path.Combine(_root, "cache", "v1", "previews");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshBytesAreSharedAndNewInstanceUsesDiskWithoutNetwork()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Image([1, 2, 3])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var first = new CachedWorkshopImageSource(origin, _root, _clock);
        var bytes = await first.GetPreviewAsync(Url, Token);
        bytes[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, await first.GetPreviewAsync(Url, Token));
        var second = new CachedWorkshopImageSource(origin, _root, _clock);
        Assert.Equal(new byte[] { 1, 2, 3 }, await second.GetPreviewAsync(Url, Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SharedPreviewOffersAReadOnlyStreamAndKeepsMutableCopiesIndependent()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Image([1, 2, 3])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        await cache.GetPreviewAsync(Url, Token);
        var update = Assert.Single(await Collect(cache.ObservePreviewAsync(Url, Token)));
        using var stream = Assert.IsType<MemoryStream>(update.OpenRead());
        Assert.False(stream.CanWrite);
        Assert.False(stream.TryGetBuffer(out _));
        var copy = update.Bytes!;
        copy[0] = 99;
        Assert.Equal(1, stream.ReadByte());
        Assert.Equal(new byte[] { 1, 2, 3 }, update.Bytes);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExpiredImageIsEmittedBeforeItsDelayedReplacementAndThenPersisted()
    {
        var replacement = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, call) => call == 1 ? Task.FromResult(Image([1])) : replacement.Task);
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, TimeSpan.FromMinutes(1));
        await cache.GetPreviewAsync(Url, Token);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await using var updates = cache.ObservePreviewAsync(Url, Token).GetAsyncEnumerator(Token);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(new byte[] { 1 }, updates.Current.Bytes);
        Assert.True(updates.Current.IsStale);
        Assert.True(updates.Current.IsRefreshing);
        var pending = updates.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        replacement.SetResult(Image([2]));
        Assert.True(await pending);
        Assert.Equal(new byte[] { 2 }, updates.Current.Bytes);
        Assert.False(updates.Current.IsStale);
        Assert.False(await updates.MoveNextAsync());
        var restarted = new CachedWorkshopImageSource(origin, _root, _clock);
        Assert.Equal(new byte[] { 2 }, await restarted.GetPreviewAsync(Url, Token));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RefreshFailureKeepsOnlyAnAllowedStaleImage()
    {
        using var handler = new Handler((_, call) => Task.FromResult(call == 1 ? Image([1]) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, TimeSpan.Zero);
        await cache.GetPreviewAsync(Url, Token);
        var updates = await Collect(cache.ObservePreviewAsync(Url, Token));
        Assert.Equal(2, updates.Count);
        Assert.Equal(new byte[] { 1 }, updates[1].Bytes);
        Assert.True(updates[1].IsStale);
        Assert.NotNull(updates[1].Error);
    }

    [Fact]
    public async Task NoStoreIsNeverSavedInMemoryOrDiskAndDeletesPreviousRepresentation()
    {
        using var handler = new Handler((_, call) =>
        {
            var response = Image([(byte)call]);
            if (call > 1) response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, TimeSpan.Zero);
        await cache.GetPreviewAsync(Url, Token);
        Assert.Equal(new byte[] { 2 }, await cache.GetPreviewAsync(Url, Token));
        Assert.Empty(Directory.EnumerateFiles(CacheDirectory, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(CacheDirectory, "*.bin"));
        Assert.Equal(new byte[] { 3 }, await cache.GetPreviewAsync(Url, Token));
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiredValidationDoesNotEmitOldBytesBeforeValidation(bool noCache)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validation = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, call) =>
        {
            if (call == 1)
            {
                var response = Image([1]);
                response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = noCache, MustRevalidate = !noCache, MaxAge = TimeSpan.Zero };
                return Task.FromResult(response);
            }
            requested.TrySetResult();
            return validation.Task;
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        await cache.GetPreviewAsync(Url, Token);
        await using var enumerator = cache.ObservePreviewAsync(Url, Token).GetAsyncEnumerator(Token);
        var first = enumerator.MoveNextAsync().AsTask();
        await requested.Task.WaitAsync(Token);
        Assert.False(first.IsCompleted);
        validation.SetResult(Image([2]));
        Assert.True(await first);
        Assert.Equal(new byte[] { 2 }, enumerator.Current.Bytes);
        Assert.False(enumerator.Current.IsStale);
    }

    [Fact]
    public async Task ETagValidationUses304BodyAndRefreshesItsFreshness()
    {
        string? conditional = null;
        using var handler = new Handler((request, call) =>
        {
            if (call == 1)
            {
                var response = Image([4, 5]);
                response.Headers.ETag = new EntityTagHeaderValue("\"version-one\"");
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(10) };
                return Task.FromResult(response);
            }
            conditional = request.Headers.IfNoneMatch.Single().Tag;
            var validated = new HttpResponseMessage(HttpStatusCode.NotModified);
            validated.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(20) };
            return Task.FromResult(validated);
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        await cache.GetPreviewAsync(Url, Token);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 4, 5 }, await cache.GetPreviewAsync(Url, Token));
        _clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(new byte[] { 4, 5 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
        Assert.Equal("\"version-one\"", conditional);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LastModifiedIsUsedWhenThereIsNoEntityTag()
    {
        var modified = _clock.GetUtcNow() - TimeSpan.FromDays(1);
        DateTimeOffset? conditional = null;
        using var handler = new Handler((request, call) =>
        {
            if (call == 1)
            {
                var response = Image([7]);
                response.Content.Headers.LastModified = modified;
                return Task.FromResult(response);
            }
            conditional = request.Headers.IfModifiedSince;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, TimeSpan.Zero);
        await cache.GetPreviewAsync(Url, Token);
        Assert.Equal(new byte[] { 7 }, await cache.GetPreviewAsync(Url, Token));
        Assert.Equal(modified, conditional);
    }

    [Fact]
    public async Task OriginalUrlValidatorsAreNotForwardedToADifferentRedirectRepresentation()
    {
        var redirected = "https://images.steamusercontent.com/replacement.png";
        using var handler = new Handler((request, call) =>
        {
            if (call == 1)
            {
                var first = Image([1]);
                first.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                return Task.FromResult(first);
            }
            if (request.RequestUri!.AbsoluteUri == Url)
            {
                Assert.Equal("\"original\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(redirected);
                return Task.FromResult(response);
            }
            Assert.Equal(redirected, request.RequestUri.AbsoluteUri);
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return Task.FromResult(Image([2]));
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, TimeSpan.Zero);
        await cache.GetPreviewAsync(Url, Token);
        Assert.Equal(new byte[] { 2 }, await cache.GetPreviewAsync(Url, Token));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task DateAndAgeReduceServerMaxAgeInsteadOfExtendingIt()
    {
        using var handler = new Handler((_, call) =>
        {
            var response = Image([(byte)call]);
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            response.Headers.Date = _clock.GetUtcNow() - TimeSpan.FromSeconds(30);
            response.Headers.Age = TimeSpan.FromSeconds(40);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        await cache.GetPreviewAsync(Url, Token);
        _clock.Advance(TimeSpan.FromSeconds(19));
        await cache.GetPreviewAsync(Url, Token);
        Assert.Equal(1, handler.Calls);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetPreviewAsync(Url, Token);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ACorruptOrOversizedIndexAndCorruptBlobAreMisses()
    {
        using var handler = new Handler((_, call) => Task.FromResult(Image([(byte)call])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token);
        var indexPath = Directory.EnumerateFiles(CacheDirectory, "*.json").Single();
        await File.WriteAllTextAsync(indexPath, new string('x', 64 * 1024 + 1), Token);
        Assert.Equal(new byte[] { 2 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
        var blob = Directory.EnumerateFiles(CacheDirectory, "*.bin").Single();
        await File.WriteAllBytesAsync(blob, [99], Token);
        Assert.Equal(new byte[] { 3 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
        await File.WriteAllTextAsync(indexPath, "{broken", Token);
        Assert.Equal(new byte[] { 4 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("schema")]
    [InlineData("contentHash")]
    public async Task IndexIdentityAndSchemaAreValidated(string field)
    {
        using var handler = new Handler((_, call) => Task.FromResult(Image([(byte)call])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token);
        var indexPath = Directory.EnumerateFiles(CacheDirectory, "*.json").Single();
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, Token))!;
        index[field] = field == "schema" ? JsonValue.Create(42) : JsonValue.Create(field == "url" ? "https://evil.example/image.png" : "../escape");
        await File.WriteAllTextAsync(indexPath, index.ToJsonString(), Token);
        Assert.Equal(new byte[] { 2 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task OldImagesPastThirtyDaysAreNotDisplayedOnFailure()
    {
        using var handler = new Handler((_, call) => Task.FromResult(call == 1 ? Image([1]) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token);
        _clock.Advance(TimeSpan.FromDays(30));
        var updates = await Collect(new CachedWorkshopImageSource(origin, _root, _clock).ObservePreviewAsync(Url, Token));
        Assert.Single(updates);
        Assert.Null(updates[0].Bytes);
        Assert.NotNull(updates[0].Error);
    }

    [Fact]
    public async Task UnwritableStorageStillReturnsOnlineImage()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "cache"), "ordinary file", Token);
        using var handler = new Handler((_, _) => Task.FromResult(Image([8])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        Assert.Equal(new byte[] { 8 }, await new CachedWorkshopImageSource(origin, _root, _clock).GetPreviewAsync(Url, Token));
    }

    [Fact]
    public async Task MemoryAndDiskLimitsAreEnforced()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Image(new byte[1024])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock, maximumDiskBytes: 3600, maximumMemoryBytes: 1024);
        for (var index = 0; index < 4; index++)
        {
            await cache.GetPreviewAsync(Url + "?item=" + index, Token);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(Directory.EnumerateFiles(CacheDirectory).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal)).Sum(path => new FileInfo(path).Length) <= 3600);
        // The oldest URL has been evicted from both memory and disk.
        await cache.GetPreviewAsync(Url + "?item=0", Token);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task SmallerBudgetTrimsOldImagesOnFirstReadWithoutDownloadingTheWholeCache()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Image(new byte[1024])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var previous = new CachedWorkshopImageSource(origin, _root, _clock);
        for (var i = 0; i < 4; i++)
        {
            await previous.GetPreviewAsync(Url + "?item=" + i, Token);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        var compact = new CachedWorkshopImageSource(origin, _root, _clock, maximumDiskBytes: 3600, maximumMemoryBytes: 0);
        Assert.Equal(1024, (await compact.GetPreviewAsync(Url + "?item=3", Token)).Length);
        Assert.Equal(4, handler.Calls);
        Assert.True(Directory.EnumerateFiles(CacheDirectory).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Sum(path => new FileInfo(path).Length) <= 3600);
        await compact.GetPreviewAsync(Url + "?item=0", Token);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task DisallowedUrlDoesNotReadCacheOrRequestNetwork()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Image([1])));
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CachedWorkshopImageSource(origin, _root, _clock)
            .GetPreviewAsync("https://example.com/image.png", Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelTheSharedDownload()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originCanceled = false;
        using var handler = new Handler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Token);
            return Image([9]);
        }, () => originCanceled = true);
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = cache.GetPreviewAsync(Url, canceled.Token);
        await entered.Task.WaitAsync(Token);
        var second = cache.GetPreviewAsync(Url, Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(originCanceled);
        release.TrySetResult();
        Assert.Equal(new byte[] { 9 }, await second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancelingAllWaitersReleasesTheDownloadForANewCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, call) =>
        {
            if (call == 1) await Task.Delay(Timeout.InfiniteTimeSpan, handlerToken.Value);
            return Image([6]);
        });
        using var http = new HttpClient(handler);
        using var origin = new SteamWorkshopImageSource(http);
        var cache = new CachedWorkshopImageSource(origin, _root, _clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = cache.GetPreviewAsync(Url, cancellation.Token);
        await handler.Entered.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(new byte[] { 6 }, await cache.GetPreviewAsync(Url, Token));
    }

    private static readonly AsyncLocal<CancellationToken> handlerToken = new();

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private static async Task<List<WorkshopPreviewUpdate>> Collect(IAsyncEnumerable<WorkshopPreviewUpdate> stream)
    {
        var result = new List<WorkshopPreviewUpdate>();
        await foreach (var update in stream) result.Add(update);
        return result;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> response, Action? onCancellation = null) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            handlerToken.Value = cancellationToken;
            var call = Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            using var registration = cancellationToken.Register(() => onCancellation?.Invoke());
            return await response(request, call).WaitAsync(cancellationToken);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
