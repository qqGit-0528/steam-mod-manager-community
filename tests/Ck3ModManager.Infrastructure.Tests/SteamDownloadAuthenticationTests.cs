using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamDownloadAuthenticationTests
{
    private static SteamSessionCredentials Credentials(string suffix = "one") => new("fixture-account", "fixture-token-" + suffix);

    [Fact]
    public async Task DifferentGameConnectionsReuseOneSuccessfulAuthorization()
    {
        var cache = new SteamDownloadAuthentication();
        var credentials = Credentials();
        Assert.Equal("ck3", await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Null(previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("ck3", credentials));
        }, TestContext.Current.CancellationToken));
        Assert.Equal("victoria3", await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(credentials, previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("victoria3", previous));
        }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("fixture-token", credentials.ToString());
        Assert.DoesNotContain("fixture-account", credentials.ToString());
    }

    [Fact]
    public async Task AFailedConnectionKeepsAuthorizationForRetry()
    {
        var cache = new SteamDownloadAuthentication();
        var credentials = Credentials();
        await cache.ConnectAsync<string>((_, _) => Task.FromResult<(string, SteamSessionCredentials?)>(("first", credentials)), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(credentials, previous);
            throw new IOException("fixture network disconnected");
        }, TestContext.Current.CancellationToken));
        await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(credentials, previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("retry", previous));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectedTokenRequestsOneNewAuthorizationAndThenReusesIt()
    {
        var cache = new SteamDownloadAuthentication();
        var expired = Credentials("expired"); var fresh = Credentials("fresh");
        await cache.ConnectAsync<string>((_, _) => Task.FromResult<(string, SteamSessionCredentials?)>(("first", expired)), TestContext.Current.CancellationToken);
        var attempts = 0;
        await cache.ConnectAsync<string>((previous, _) =>
        {
            attempts++;
            if (previous is not null) { Assert.Same(expired, previous); throw new SteamSessionRejectedException("Expired"); }
            return Task.FromResult<(string, SteamSessionCredentials?)>(("reauthorized", fresh));
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
        await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(fresh, previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("third", previous));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentGamesWaitForTheFirstAuthorizationAndCancelledWaitDoesNotClearIt()
    {
        var cache = new SteamDownloadAuthentication(); var credentials = Credentials();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.ConnectAsync<string>(async (_, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token); return ("ck3", credentials);
        }, TestContext.Current.CancellationToken);
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = cache.ConnectAsync<string>((_, _) => throw new InvalidOperationException("Cancelled waiter must not start login"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var second = cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(credentials, previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("victoria3", previous));
        }, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal("ck3", await first); Assert.Equal("victoria3", await second);
    }

    [Fact]
    public async Task ClearingWhileLoginIsPendingDoesNotRestoreCredentialsAfterExit()
    {
        var cache = new SteamDownloadAuthentication();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.ConnectAsync<string>(async (_, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token); return ("pending", Credentials());
        }, TestContext.Current.CancellationToken);
        await started.Task; cache.Clear(); release.SetResult(); await pending;
        await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Null(previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("next", null));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposingOneGameProviderKeepsSharedAuthorizationForOtherGames()
    {
        var cache = new SteamDownloadAuthentication(); var credentials = Credentials();
        await cache.ConnectAsync<string>((_, _) => Task.FromResult<(string, SteamSessionCredentials?)>(("first", credentials)), TestContext.Current.CancellationToken);
        using (var provider = new SteamWorkshopContentProvider(authentication: cache)) { }
        await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Same(credentials, previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("other", previous));
        }, TestContext.Current.CancellationToken);
        cache.Clear();
        await cache.ConnectAsync<string>((previous, _) =>
        {
            Assert.Null(previous);
            return Task.FromResult<(string, SteamSessionCredentials?)>(("new session", null));
        }, TestContext.Current.CancellationToken);
    }
}
