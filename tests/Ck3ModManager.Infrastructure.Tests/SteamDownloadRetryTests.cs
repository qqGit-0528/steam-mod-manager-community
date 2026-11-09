using System.Net;
using System.Reflection;
using Ck3ModManager.Infrastructure;
using SteamKit2;
using SteamKit2.CDN;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamDownloadRetryTests
{
    [Fact]
    public async Task SdkInternalTimeoutTriesNextCdnWithoutCancellingUserTask()
    {
        var configuration = SteamConfiguration.Create(builder => builder.WithHttpClientFactory(_ => new HttpClient(new TimeoutHandler())));
        using var cdn = new Client(new SteamClient(configuration));
        var attempts = 0;
        Server first = new DnsEndPoint("first.invalid", 443);
        Server second = new DnsEndPoint("second.invalid", 443);
        Func<Server, string?, Task<int>> action = (server, _) => ++attempts == 1
            ? cdn.DownloadDepotChunkAsync(0, new DepotManifest.ChunkData { ChunkID = [1] }, server, []) : Task.FromResult(123);
        var result = await InvokeRetry([first, second], action, TestContext.Current.CancellationToken);
        Assert.Equal(123, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExplicitUserCancellationDoesNotTryAnotherServer()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        Func<Server, string?, Task<int>> action = (_, _) =>
        {
            attempts++; cancellation.Cancel();
            return Task.FromCanceled<int>(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeRetry([new(), new()], action, cancellation.Token));
        Assert.Equal(1, attempts);
    }

    private static Task<int> InvokeRetry(IReadOnlyList<Server> servers, Func<Server, string?, Task<int>> action, CancellationToken token)
    {
        // 此测试只覆盖无认证挑战的 CDN 网络路径，不需要真实 Steam 会话。
        var method = typeof(SteamWorkshopContentProvider).GetMethod("FromCdnAsync", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int));
        return (Task<int>)method.Invoke(null, [null, 0U, 0U, servers, action, token])!;
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("SDK 内部连接超时，用户未取消"));
    }
}
