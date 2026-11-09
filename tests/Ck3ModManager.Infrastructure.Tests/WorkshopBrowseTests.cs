using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopBrowseTests
{
    [Theory]
    [InlineData("hoi4")]
    [InlineData("eu4")]
    [InlineData("stellaris")]
    [InlineData("victoria3")]
    [InlineData("imperator")]
    public async Task SelectedGameControlsBrowseUriQueryAndConsumerValidation(string id)
    {
        var game = GameCatalog.Get(id);
        var request = new WorkshopSearchRequest();
        var uri = SteamWorkshopBrowser.BuildUri(request, game);
        Assert.Equal(game.AppId.ToString(), Assert.Single(QueryParameters(uri), pair => pair.Key == "appid").Value);
        var html = Render(Data(Item(appId: game.AppId)), queryKey: ["workshop_browse", new { appid = game.AppId }]);
        using var handler = new ControlledHandler((message, _) =>
        {
            Assert.Equal(uri, message.RequestUri);
            return Task.FromResult(HtmlResponse(html));
        });
        using var client = new HttpClient(handler);

        var page = await new SteamWorkshopBrowser(client, game: game).SearchAsync(request);

        Assert.Equal(game.AppId, Assert.Single(page.Items).ConsumerAppId);
        Assert.Throws<InvalidOperationException>(() => SteamWorkshopBrowser.ParsePage(Render(Data(Item())), 1, game));
        Assert.Throws<InvalidOperationException>(() => SteamWorkshopBrowser.ParsePage(
            Render(Data(Item()), queryKey: ["workshop_browse", new { appid = game.AppId }]), 1, game));
    }

    [Fact]
    public void EncodesSearchAndBothTagsWithoutAllowingQueryInjection()
    {
        const string search = "汉化 &appid=570#结果?";
        const string category = "Maps & Graphics";
        const string version = "1.17.*";
        var uri = SteamWorkshopBrowser.BuildUri(new WorkshopSearchRequest(
            $"  {search}  ", WorkshopSort.MostSubscribed, 3, category, version));

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("steamcommunity.com", uri.Host);
        Assert.Equal("/workshop/browse/", uri.AbsolutePath);
        Assert.Empty(uri.Fragment);
        var parameters = QueryParameters(uri);
        Assert.Equal(Ck3.AppId.ToString(), Assert.Single(parameters, pair => pair.Key == "appid").Value);
        Assert.Equal(search, Assert.Single(parameters, pair => pair.Key == "searchtext").Value);
        Assert.Equal(new[] { category, version }, parameters.Where(pair => pair.Key == "requiredtags[]").Select(pair => pair.Value));
        Assert.Equal("3", Assert.Single(parameters, pair => pair.Key == "p").Value);
        Assert.Equal("totaluniquesubscribers", Assert.Single(parameters, pair => pair.Key == "browsesort").Value);
    }

    [Theory]
    [InlineData(WorkshopSort.Trending, "trend")]
    [InlineData(WorkshopSort.MostSubscribed, "totaluniquesubscribers")]
    [InlineData(WorkshopSort.MostRecent, "mostrecent")]
    [InlineData(WorkshopSort.RecentlyUpdated, "lastupdated")]
    public void UsesSteamSortNames(WorkshopSort sort, string expected)
    {
        var parameters = QueryParameters(SteamWorkshopBrowser.BuildUri(new WorkshopSearchRequest(Sort: sort)));
        Assert.Equal(expected, Assert.Single(parameters, pair => pair.Key == "browsesort").Value);
        Assert.DoesNotContain(parameters, pair => pair.Key is "searchtext" or "requiredtags[]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidRequestedPages(int page) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamWorkshopBrowser.BuildUri(new WorkshopSearchRequest(Page: page)));

    [Fact]
    public void RejectsUnknownSort() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamWorkshopBrowser.BuildUri(new WorkshopSearchRequest(Sort: (WorkshopSort)999)));

    [Fact]
    public void ParsesStringEncodedSsrQueryAndKeepsItemMetadataAndDeclaredTags()
    {
        var item = Item();
        item["title"] = "地图 &amp; 汉化";
        item["file_size"] = "4096";
        item["subscriptions"] = 1234;
        var data = Data(item);
        data["current_page"] = 2;
        data["total_pages"] = 5;
        data["total_count"] = 45;
        var loaderData = new[] { JsonSerializer.Serialize(new
        {
            declaredTags = new
            {
                readytouse_tags = new object[]
                {
                    new { name = "Category", tags = new[] { new { name = "Events" }, new { name = "Graphics" } } },
                    new { name = "Compatible Version", tags = new[] { new { name = "1.17" } } }
                }
            }
        }) };

        var result = SteamWorkshopBrowser.ParsePage(Render(data, loaderData: loaderData), 2);
        Assert.Equal(2, result.Page);
        Assert.Equal(5, result.TotalPages);
        Assert.Equal(45, result.TotalCount);
        var parsed = Assert.Single(result.Items);
        Assert.Equal(123UL, parsed.PublishedFileId);
        Assert.Equal(Ck3.AppId, parsed.ConsumerAppId);
        Assert.Equal("地图 & 汉化", parsed.Title);
        Assert.Equal("简介", parsed.Description);
        Assert.Equal("https://images.steamusercontent.com/preview.png", parsed.PreviewImageUrl);
        Assert.Equal(4096, parsed.FileSizeBytes);
        Assert.Equal(1234, parsed.Subscriptions);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), parsed.UpdatedAt);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=123", parsed.WorkshopUrl.AbsoluteUri);
        Assert.Equal(new[] { "Events", "1.16" }, parsed.Tags);
        Assert.Null(parsed.RequiredItemIds);
        Assert.Equal(new[] { "Events", "Graphics" }, result.CategoryTags);
        Assert.Contains("1.17", result.VersionTags);
        Assert.Contains("1.16", result.VersionTags);
    }

    [Fact]
    public void UsesCk3BrowseQueryWhenOtherQueriesArePresent()
    {
        var otherGame = Query(Data(Item(appId: 570)), ["workshop_browse", new { appid = 570 }]);
        var unrelated = Query(Data(), ["other_query", new { appid = Ck3.AppId }]);
        var ck3 = Query(Data(Item()), ["workshop_browse", new { appid = Ck3.AppId }]);
        var result = SteamWorkshopBrowser.ParsePage(Render(Data(), queries: [otherGame, unrelated, ck3]), 1);
        Assert.Equal(Ck3.AppId, Assert.Single(result.Items).ConsumerAppId);
    }

    [Fact]
    public void AcceptsARealEmptyResultWithoutInventingItems()
    {
        var data = Data();
        data["total_pages"] = 0;
        var result = SteamWorkshopBrowser.ParsePage(Render(data), 1);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(1, result.TotalPages);
    }

    [Theory]
    [InlineData(570U)]
    [InlineData(0U)]
    public void RejectsItemsForOtherGames(uint appId) =>
        AssertPageFormatError(Render(Data(Item(appId: appId))), 1);

    [Fact]
    public void RejectsOtherGameQueryEvenWhenItsItemsClaimCk3() =>
        AssertPageFormatError(Render(Data(Item()), queryKey: ["workshop_browse", new { appid = 570 }]), 1);

    [Theory]
    [InlineData("0")]
    [InlineData("18446744073709551616")]
    [InlineData("not-an-id")]
    public void RejectsInvalidItemIds(string id) => AssertPageFormatError(Render(Data(Item(id))), 1);

    [Fact]
    public void RejectsReturnedPageMismatch() => AssertPageFormatError(Render(Data(Item())), 2);

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void RejectsUnsuccessfulSteamResults(int eresult)
    {
        var data = Data();
        data["eresult"] = eresult;
        AssertPageFormatError(Render(data), 1);
    }

    [Fact]
    public void RejectsMissingResultsInsteadOfReportingEmpty()
    {
        var data = Data();
        data.Remove("results");
        AssertPageFormatError(Render(data), 1);
    }

    [Theory]
    [InlineData("<html><body>Steam login or unavailable</body></html>")]
    [InlineData("<script id='valve-ssr-data'>not-json</script>")]
    [InlineData("<script id='valve-ssr-data'>{\"renderContext\":{\"queryData\":{}}}</script>")]
    public void ReportsChangedPageFormatWithActionableMessage(string html) => AssertPageFormatError(html, 1);

    [Fact]
    public void ReportsTruncatedQueryKeyAsPageFormatChange() =>
        AssertPageFormatError(Render(Data(), queryKey: ["workshop_browse"]), 1);

    [Fact]
    public void ReportsEmptyQueryKeyAsPageFormatChange() =>
        AssertPageFormatError(Render(Data(), queryKey: []), 1);

    [Fact]
    public void ReportsNullPublishedFileIdAsPageFormatChange()
    {
        var item = Item();
        item["publishedfileid"] = null;
        AssertPageFormatError(Render(Data(item)), 1);
    }

    [Fact]
    public void ReportsNullQueryDataAsPageFormatChange() => AssertPageFormatError(
        "<script id='valve-ssr-data'>{\"renderContext\":{\"queryData\":null}}</script>", 1);

    [Fact]
    public async Task SearchReadsOnlyTheBuiltSteamAddressAndParsesItsResponse()
    {
        var request = new WorkshopSearchRequest("汉化", WorkshopSort.RecentlyUpdated, 1, "Events", "1.17");
        using var handler = new ControlledHandler((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal(SteamWorkshopBrowser.BuildUri(request), message.RequestUri);
            return Task.FromResult(HtmlResponse(Render(Data(Item()))));
        });
        using var client = new HttpClient(handler);
        var result = await new SteamWorkshopBrowser(client).SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(result.Items);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SearchPropagatesCallerCancellationDuringTransport()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ControlledHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return HtmlResponse(Render(Data()));
        });
        using var client = new HttpClient(handler);
        var pending = new SteamWorkshopBrowser(client).SearchAsync(new WorkshopSearchRequest(), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task SearchRejectsOversizedBodyEvenWithSmallDeclaredLength()
    {
        using var handler = new ControlledHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) };
            response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new SteamWorkshopBrowser(client).SearchAsync(new WorkshopSearchRequest(), TestContext.Current.CancellationToken));
        Assert.Contains("过大", error.Message);
    }

    private static void AssertPageFormatError(string html, int page)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SteamWorkshopBrowser.ParsePage(html, page));
        Assert.Contains("页面格式", error.Message);
        Assert.Contains("Steam", error.Message);
    }

    private static Dictionary<string, object?> Item(string id = "123", uint appId = Ck3.AppId) => new()
    {
        ["publishedfileid"] = id,
        ["consumer_appid"] = appId,
        ["creator_appid"] = 766,
        ["title"] = "测试 Mod",
        ["short_description"] = "简介",
        ["preview_url"] = "https://images.steamusercontent.com/preview.png",
        ["time_updated"] = 1700000000,
        ["file_size"] = 100,
        ["tags"] = new[] { new { tag = "Events" }, new { tag = "1.16" } }
    };

    private static Dictionary<string, object?> Data(params object[] items) => new()
    {
        ["eresult"] = 1, ["current_page"] = 1, ["total_pages"] = 1,
        ["total_count"] = items.Length, ["results"] = items
    };

    private static object Query(object data, object[] key) => new { queryKey = key, state = new { data } };

    private static string Render(object data, object[]? queryKey = null, string[]? loaderData = null, object[]? queries = null)
    {
        var payload = new
        {
            renderContext = new
            {
                // Steam 将 queryData 再序列化成字符串，并不是直接内嵌对象。
                queryData = JsonSerializer.Serialize(new
                {
                    queries = queries ?? [Query(data, queryKey ?? ["workshop_browse", new { appid = Ck3.AppId }])]
                })
            },
            loaderData = loaderData ?? []
        };
        return $"<html><script type='application/json' id='valve-ssr-data'>{JsonSerializer.Serialize(payload)}</script></html>";
    }

    private static List<KeyValuePair<string, string>> QueryParameters(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .Select(pair => new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))).ToList();

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }
}

public sealed class WorkshopImageSourceTests
{
    private const string ImageUrl = "https://images.steamusercontent.com/preview.png";

    [Theory]
    [InlineData("https://steamusercontent.com/a.png")]
    [InlineData(ImageUrl)]
    [InlineData("https://steamuserimages-a.akamaihd.net/a.png")]
    [InlineData("https://cdn.akamai.steamstatic.com/a.png")]
    [InlineData("https://shared.akamai.steamstatic.com/a.png")]
    public void AllowsRecognizedHttpsSteamImageHosts(string url) => Assert.True(SteamWorkshopImageSource.IsAllowedUrl(new Uri(url)));

    [Theory]
    [InlineData("http://steamusercontent.com/a.png")]
    [InlineData("https://example.com/a.png")]
    [InlineData("https://steamusercontent.com.evil.example/a.png")]
    [InlineData("https://steamuserimages-a.akamaihd.net.evil.example/a.png")]
    [InlineData("https://user@steamusercontent.com/a.png")]
    [InlineData("https://steamusercontent.com:444/a.png")]
    [InlineData("file:///preview.png")]
    public void RejectsUntrustedImageSources(string url) => Assert.False(SteamWorkshopImageSource.IsAllowedUrl(new Uri(url)));

    [Fact]
    public void RejectsRelativeImageUri() => Assert.False(SteamWorkshopImageSource.IsAllowedUrl(new Uri("/preview.png", UriKind.Relative)));

    [Fact]
    public async Task InvalidInitialUrlDoesNotPerformHttpRequest()
    {
        using var handler = new ImageHandler((_, _) => throw new InvalidOperationException("不应访问网络"));
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPreviewAsync("https://example.com/a.png", TestContext.Current.CancellationToken));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task ReadsImageBytesFromAllowedHost()
    {
        byte[] expected = [137, 80, 78, 71, 13, 10];
        using var handler = new ImageHandler((_, _) => Task.FromResult(ImageResponse(expected)));
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        Assert.Equal(expected, await source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsRedirectBeforeRequestingUntrustedTarget()
    {
        using var handler = new ImageHandler((_, _) => Task.FromResult(Redirect("https://example.com/pixel.png")));
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
        Assert.Contains("跳转", error.Message);
        Assert.Equal(new[] { ImageUrl }, handler.RequestedUrls.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task FollowsAllowedAndRelativeRedirectsAfterValidation()
    {
        using var handler = new ImageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/preview.png" => Redirect("https://shared.akamai.steamstatic.com/cover.png"),
            "/cover.png" => Redirect("/final.png"),
            "/final.png" => ImageResponse([1, 2, 3]),
            _ => throw new InvalidOperationException("未预期的请求")
        }));
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        Assert.Equal(new byte[] { 1, 2, 3 }, await source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { ImageUrl, "https://shared.akamai.steamstatic.com/cover.png", "https://shared.akamai.steamstatic.com/final.png" },
            handler.RequestedUrls.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task StopsRedirectLoop()
    {
        using var handler = new ImageHandler((_, _) => Task.FromResult(Redirect(ImageUrl)));
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
        Assert.Contains("跳转", error.Message);
        Assert.Equal(4, handler.RequestedUrls.Count);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task RejectsNonImageResponse(string? contentType)
    {
        using var handler = new ImageHandler((_, _) =>
        {
            var response = ImageResponse([1, 2, 3]);
            response.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedImageByHeaderOrActualBody(bool accurateLength)
    {
        using var handler = new ImageHandler((_, _) =>
        {
            var response = ImageResponse(new byte[5 * 1024 * 1024 + 1]);
            if (!accurateLength) response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetPreviewAsync(ImageUrl, TestContext.Current.CancellationToken));
        Assert.Contains("过大", error.Message);
    }

    [Fact]
    public async Task CancellationReleasesPreviewDownloadSlot()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var handler = new ImageHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref started) <= 4)
            {
                if (Volatile.Read(ref started) == 4) entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return ImageResponse([7]);
        });
        using var client = new HttpClient(handler);
        using var source = new SteamWorkshopImageSource(client);
        var pending = Enumerable.Range(0, 4).Select(_ => source.GetPreviewAsync(ImageUrl, cancellation.Token)).ToArray();
        await entered.Task;
        cancellation.Cancel();
        foreach (var request in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        verification.CancelAfter(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 7 }, await source.GetPreviewAsync(ImageUrl, verification.Token));
    }

    [Fact]
    public async Task ImageTimeoutCoversBodyAfterSuccessfulHeadersAndReleasesDownloadSlots()
    {
        var requests = 0;
        var bodyReads = 0;
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(3));
        using var handler = new ImageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) > 4) return Task.FromResult(ImageResponse([9]));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UntilCancellationStream(() => Interlocked.Increment(ref bodyReads)))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        });
        // 不让 HttpClient 自身超时替 source 的 body 超时兜底。
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new SteamWorkshopImageSource(client, TimeSpan.FromMilliseconds(100));
        var pending = Enumerable.Range(0, 4).Select(_ => source.GetPreviewAsync(ImageUrl, safety.Token)).ToArray();

        foreach (var request in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.False(safety.IsCancellationRequested);
        Assert.Equal(4, bodyReads);
        // 四个槽都走过 body 超时路径后，下一张图片应仍可立即下载。
        Assert.Equal(new byte[] { 9 }, await source.GetPreviewAsync(ImageUrl, safety.Token));
        Assert.Equal(5, requests);
    }

    private sealed class UntilCancellationStream(Action onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> RequestedUrls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!);
            return respond(request, cancellationToken);
        }
    }
}
