using System.Net;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamWorkshopDependencySourceTests
{
    [Fact]
    public async Task ValidatesSteamIdentityAndDistinguishesEmptyFromUnknown()
    {
        const string main = """<a href="https://steamcommunity.com/sharedfiles/filedetails/?id=1" class="sectionTab active description"><span>Description</span></a>""";
        const string sidebar = """<div id="sharedfiles_content_ctn" data-miniprofile-appid="1158310"><div id="rightContents">""";
        const string required = """
            <div class="requiredItemsContainer" id="RequiredItems">
              <a href="https://steamcommunity.com/workshop/filedetails/?id=42" target="_blank"><div class="requiredItem">One</div></a>
              <a href="https://steamcommunity.com/workshop/filedetails/?id=43"><div class="requiredItem">Two</div></a>
              <a href="https://steamcommunity.com/workshop/filedetails/?id=42"><div class="requiredItem">One</div></a>
            </div>
            """;
        const string end = """<div class="rightSectionTopTitle condensed">Created by</div>""";
        var token = TestContext.Current.CancellationToken;
        var handler = new Handler { Body = main + sidebar + required + end };
        using var http = new HttpClient(handler);
        var source = new SteamWorkshopDependencySource(http);
        Assert.Equal(new ulong[] { 42, 43 }, await source.ReadRequiredItemsAsync(1158310, 1, token));
        Assert.Null(await source.ReadRequiredItemsAsync(1158310, 2, token));
        Assert.Null(await source.ReadRequiredItemsAsync(529340, 1, token));

        handler.Body = main + sidebar + end;
        Assert.Empty((await source.ReadRequiredItemsAsync(1158310, 1, token))!);
        handler.Body = main + sidebar + required.Replace("steamcommunity.com", "example.org") + end;
        Assert.Null(await source.ReadRequiredItemsAsync(1158310, 1, token));
        handler.Status = HttpStatusCode.InternalServerError;
        Assert.Null(await source.ReadRequiredItemsAsync(1158310, 1, token));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body), RequestMessage = request });
    }
}
