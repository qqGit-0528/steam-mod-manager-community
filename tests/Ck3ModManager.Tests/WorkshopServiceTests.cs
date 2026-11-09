using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkshopServiceTests
{
    [Fact]
    public async Task SelectedGameAcceptsItsOwnResourcesAndRejectsCk3DetailsAndLists()
    {
        var item = MakeItem(consumerAppId: GameCatalog.Hoi4.AppId);
        var source = new WorkshopSource((_, _) => Task.FromResult(item));
        var browser = new WorkshopBrowser(() => new WorkshopBrowsePage([item], 1, 1, 1, [], []));
        var service = new WorkshopService(source, browser, game: GameCatalog.Hoi4);

        Assert.Same(GameCatalog.Hoi4, service.Game);
        Assert.Same(item, await service.LookupAsync("123"));
        Assert.Same(item, Assert.Single((await service.SearchAsync(new WorkshopSearchRequest())).Items));

        item = MakeItem();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LookupAsync("123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(new WorkshopSearchRequest()));
    }

    [Fact]
    public async Task LooksUpParsedIdAndPreservesSourceItem()
    {
        using var cancellation = new CancellationTokenSource();
        var item = MakeItem();
        var source = new WorkshopSource((id, token) =>
        {
            Assert.Equal(123UL, id);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(item);
        });
        var result = await new WorkshopService(source).LookupAsync(
            "https://steamcommunity.com/sharedfiles/filedetails/?id=123", cancellation.Token);
        Assert.Same(item, result);
    }

    [Fact]
    public async Task RejectsNonCk3WorkshopResource()
    {
        var source = new WorkshopSource((_, _) => Task.FromResult(MakeItem(consumerAppId: 294100)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkshopService(source).LookupAsync("123"));
        Assert.Contains("不属于", error.Message);
    }

    [Fact]
    public async Task RejectsSourceReturningDifferentItem()
    {
        var source = new WorkshopSource((_, _) => Task.FromResult(MakeItem(publishedFileId: 456)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkshopService(source).LookupAsync("123"));
    }

    [Fact]
    public async Task InvalidReferenceDoesNotCallSource()
    {
        var source = new WorkshopSource((_, _) => throw new InvalidOperationException("不应请求网络"));
        await Assert.ThrowsAsync<FormatException>(() => new WorkshopService(source).LookupAsync("0"));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task PreCanceledLookupDoesNotCallSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new WorkshopSource((_, _) => throw new InvalidOperationException("不应请求网络"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkshopService(source).LookupAsync("123", cancellation.Token));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task PassesCancellationIntoInFlightSourceRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new WorkshopSource(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return MakeItem();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkshopService(source).LookupAsync("123", cancellation.Token));
    }

    private static WorkshopItem MakeItem(ulong publishedFileId = 123, uint consumerAppId = Ck3.AppId) =>
        new(publishedFileId, consumerAppId, "示例 Mod", "说明", null,
            new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}"),
            DateTimeOffset.UnixEpoch, 1, [], []);

    private sealed class WorkshopSource(Func<ulong, CancellationToken, Task<WorkshopItem>> getItem) : IWorkshopSource
    {
        public int Calls { get; private set; }
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return getItem(publishedFileId, cancellationToken);
        }
    }

    private sealed class WorkshopBrowser(Func<WorkshopBrowsePage> getPage) : IWorkshopBrowser
    {
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(getPage());
    }
}
