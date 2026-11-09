using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopCacheTests
{
    [Fact]
    public async Task RequestsWithTheSameBuiltAddressReuseTheResponse()
    {
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client);
        var firstRequest = new WorkshopSearchRequest("  汉化  ", CategoryTag: "Events", VersionTag: "1.20");
        var equivalentRequest = firstRequest with { SearchText = "汉化" };
        Assert.Equal(SteamWorkshopBrowser.BuildUri(firstRequest), SteamWorkshopBrowser.BuildUri(equivalentRequest));

        var first = await browser.SearchAsync(firstRequest, TestContext.Current.CancellationToken);
        var cached = await browser.SearchAsync(equivalentRequest, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(Assert.Single(first.Items).PublishedFileId, Assert.Single(cached.Items).PublishedFileId);
        Assert.Equal(Assert.Single(first.Items).Title, Assert.Single(cached.Items).Title);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("sort")]
    [InlineData("page")]
    [InlineData("category")]
    [InlineData("version")]
    public async Task DifferentSearchParametersKeepIndependentCachedPages(string changedParameter)
    {
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client);
        var firstRequest = new WorkshopSearchRequest("地图", WorkshopSort.Trending, 1, "Events", "1.20");
        var secondRequest = changedParameter switch
        {
            "search" => firstRequest with { SearchText = "汉化" },
            "sort" => firstRequest with { Sort = WorkshopSort.MostSubscribed },
            "page" => firstRequest with { Page = 2 },
            "category" => firstRequest with { CategoryTag = "Graphics" },
            "version" => firstRequest with { VersionTag = "1.19" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedParameter))
        };

        var first = await browser.SearchAsync(firstRequest, TestContext.Current.CancellationToken);
        var second = await browser.SearchAsync(secondRequest, TestContext.Current.CancellationToken);
        var firstAgain = await browser.SearchAsync(firstRequest, TestContext.Current.CancellationToken);
        var secondAgain = await browser.SearchAsync(secondRequest, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(firstRequest.Page, first.Page);
        Assert.Equal(secondRequest.Page, second.Page);
        Assert.Equal(1UL, Assert.Single(first.Items).PublishedFileId);
        Assert.Equal(2UL, Assert.Single(second.Items).PublishedFileId);
        Assert.Equal(1UL, Assert.Single(firstAgain.Items).PublishedFileId);
        Assert.Equal(2UL, Assert.Single(secondAgain.Items).PublishedFileId);
    }

    [Fact]
    public async Task HttpFailuresAreRetriedAndOnlyTheSuccessfulResponseIsCached()
    {
        using var handler = new MemoryHandler((message, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : HtmlResponse(RenderPage(RequestedPage(message), call)));
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client);
        var request = new WorkshopSearchRequest("地图");

        await Assert.ThrowsAsync<HttpRequestException>(() => browser.SearchAsync(request, TestContext.Current.CancellationToken));
        var recovered = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        var cached = await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(2UL, Assert.Single(recovered.Items).PublishedFileId);
        Assert.Equal(2UL, Assert.Single(cached.Items).PublishedFileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSsrOrWrongPageResponsesAreNotCached(bool returnWrongPage)
    {
        using var handler = new MemoryHandler((message, call) => HtmlResponse(call == 1
            ? (returnWrongPage ? RenderPage(RequestedPage(message) + 1, call) : "<html>Steam 暂时不可用</html>")
            : RenderPage(RequestedPage(message), call)));
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client);
        var request = new WorkshopSearchRequest("地图");

        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.SearchAsync(request, TestContext.Current.CancellationToken));
        var recovered = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        var cached = await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(request.Page, recovered.Page);
        Assert.Equal(2UL, Assert.Single(recovered.Items).PublishedFileId);
        Assert.Equal(2UL, Assert.Single(cached.Items).PublishedFileId);
    }

    [Fact]
    public async Task CanceledCallerCannotReadACachedPage()
    {
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client);
        var request = new WorkshopSearchRequest("地图");
        await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.SearchAsync(request, cancellation.Token));
        var cached = await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(1UL, Assert.Single(cached.Items).PublishedFileId);
    }

    [Fact]
    public async Task DefaultCacheExpiresAtTwoMinutesEvenWhenReadBeforeExpiry()
    {
        var clock = new ManualTimeProvider();
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client, timeProvider: clock);
        var request = new WorkshopSearchRequest("地图");
        await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(119));
        var cached = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1UL, Assert.Single(cached.Items).PublishedFileId);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2UL, Assert.Single(refreshed.Items).PublishedFileId);
    }

    [Fact]
    public async Task CustomDurationControlsExpiry()
    {
        var clock = new ManualTimeProvider();
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client, TimeSpan.FromSeconds(10), clock);
        var request = new WorkshopSearchRequest();
        await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(9));
        await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2UL, Assert.Single(refreshed.Items).PublishedFileId);
    }

    [Fact]
    public async Task ZeroDurationDisablesCaching()
    {
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client, TimeSpan.Zero, new ManualTimeProvider());
        var request = new WorkshopSearchRequest();

        var first = await browser.SearchAsync(request, TestContext.Current.CancellationToken);
        var second = await browser.SearchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(1UL, Assert.Single(first.Items).PublishedFileId);
        Assert.Equal(2UL, Assert.Single(second.Items).PublishedFileId);
    }

    [Fact]
    public async Task PageCapacityEvictsTheOldestWhileKeepingTheOtherCachedPages()
    {
        var clock = new ManualTimeProvider();
        using var handler = new MemoryHandler();
        using var client = new HttpClient(handler);
        var browser = new SteamWorkshopBrowser(client, timeProvider: clock);
        for (var page = 1; page <= WorkshopCacheLimits.MemoryPages; page++)
        {
            await browser.SearchAsync(new WorkshopSearchRequest(Page: page), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        for (var page = 1; page <= WorkshopCacheLimits.MemoryPages; page++)
            await browser.SearchAsync(new WorkshopSearchRequest(Page: page), TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopCacheLimits.MemoryPages, handler.Calls);

        await browser.SearchAsync(new WorkshopSearchRequest(Page: WorkshopCacheLimits.MemoryPages + 1), TestContext.Current.CancellationToken);
        for (var page = 2; page <= WorkshopCacheLimits.MemoryPages + 1; page++)
        {
            var retained = await browser.SearchAsync(new WorkshopSearchRequest(Page: page), TestContext.Current.CancellationToken);
            Assert.Equal((ulong)page, Assert.Single(retained.Items).PublishedFileId);
        }
        Assert.Equal(WorkshopCacheLimits.MemoryPages + 1, handler.Calls);

        var reloaded = await browser.SearchAsync(new WorkshopSearchRequest(Page: 1), TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopCacheLimits.MemoryPages + 2, handler.Calls);
        Assert.Equal(1, reloaded.Page);
        Assert.Equal((ulong)WorkshopCacheLimits.MemoryPages + 2, Assert.Single(reloaded.Items).PublishedFileId);
    }

    private static int RequestedPage(HttpRequestMessage request) => int.Parse(
        request.RequestUri!.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("p=", StringComparison.Ordinal))[2..],
        CultureInfo.InvariantCulture);

    private static string RenderPage(int page, int responseNumber)
    {
        var data = new
        {
            eresult = 1,
            current_page = page,
            total_pages = 100,
            total_count = 100,
            results = new[]
            {
                new
                {
                    publishedfileid = responseNumber.ToString(CultureInfo.InvariantCulture),
                    consumer_appid = Ck3.AppId,
                    title = $"测试响应 {responseNumber}",
                    short_description = "内存中的工坊条目",
                    time_updated = 1700000000,
                    file_size = 100,
                    tags = new[] { new { tag = "1.20" } }
                }
            }
        };
        var payload = new
        {
            renderContext = new
            {
                queryData = JsonSerializer.Serialize(new
                {
                    queries = new[]
                    {
                        new { queryKey = new object[] { "workshop_browse", new { appid = Ck3.AppId } }, state = new { data } }
                    }
                })
            }
        };
        return $"<script id='valve-ssr-data' type='application/json'>{JsonSerializer.Serialize(payload)}</script>";
    }

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private sealed class MemoryHandler(Func<HttpRequestMessage, int, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond?.Invoke(request, Calls) ?? HtmlResponse(RenderPage(RequestedPage(request), Calls)));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
