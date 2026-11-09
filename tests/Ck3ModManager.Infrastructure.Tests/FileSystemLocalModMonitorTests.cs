using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class FileSystemLocalModMonitorTests
{
    [Fact]
    public async Task UnchangedDirectoriesDoNotCauseAdditionalScans()
    {
        using var fixture = new Fixture();
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(watch.Current.Mods);

        var next = watch.NextAsync();
        await Task.Delay(180, TestContext.Current.CancellationToken);

        Assert.False(next.IsCompleted);
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(1, fixture.DiscoveryCalls);
    }

    [Fact]
    public async Task InstallingAndRemovingAModPublishTheChangedInstallationSet()
    {
        using var fixture = new Fixture();
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        var installed = watch.NextAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Target, "new.mod"), "新 Mod", TestContext.Current.CancellationToken);
        Assert.True(await installed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("new.mod", Assert.Single(watch.Current.Mods).Id);

        var removed = watch.NextAsync();
        File.Delete(Path.Combine(fixture.Target, "new.mod"));
        Assert.True(await removed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(watch.Current.Mods);
    }

    [Fact]
    public async Task ExistingModMetadataEditsPublishAnotherScanWithTheSameIdentity()
    {
        using var fixture = new Fixture();
        var descriptor = Path.Combine(fixture.Target, "existing.mod");
        await File.WriteAllTextAsync(descriptor, "旧名称 1.0", TestContext.Current.CancellationToken);
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var initial = Assert.Single(watch.Current.Mods);

        var next = watch.NextAsync();
        await File.WriteAllTextAsync(descriptor, "新名称 2.0", TestContext.Current.CancellationToken);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var updated = Assert.Single(watch.Current.Mods);
        Assert.Equal(initial.Id, updated.Id);
        Assert.Equal("新名称 2.0", updated.Name);
    }

    [Fact]
    public async Task MissingTargetIsDetectedWhenItsAncestorsAndThenTheTargetAreCreated()
    {
        using var fixture = new Fixture(createTarget: false);
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(watch.Current.Mods);
        var next = watch.NextAsync();

        Directory.CreateDirectory(Path.Combine(fixture.Root, "selected-game"));
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "selected-game", "workshop"));
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(fixture.Target);
        await File.WriteAllTextAsync(Path.Combine(fixture.Target, "late.mod"), "首次下载", TestContext.Current.CancellationToken);

        // 观察器现在转发原始扫描，父目录逐级建立时可以先收到空快照。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await next.WaitAsync(deadline.Token));
        while (watch.Current.Mods.Count == 0)
            Assert.True(await watch.NextAsync().WaitAsync(deadline.Token));
        Assert.Equal("late.mod", Assert.Single(watch.Current.Mods).Id);
    }

    [Fact]
    public async Task ChangesForAnotherGameDoNotTriggerDiscoveryOrScanning()
    {
        using var fixture = new Fixture(createTarget: false);
        var otherGame = Path.Combine(fixture.Root, "other-game", "mod");
        Directory.CreateDirectory(otherGame);
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var next = watch.NextAsync();

        await File.WriteAllTextAsync(Path.Combine(otherGame, "unrelated.mod"), "其他游戏", TestContext.Current.CancellationToken);
        await Task.Delay(180, TestContext.Current.CancellationToken);

        Assert.False(next.IsCompleted);
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(1, fixture.DiscoveryCalls);
    }

    [Fact]
    public async Task CancellationReleasesWatchingAndLaterFilesDoNotCauseMoreScans()
    {
        using var fixture = new Fixture();
        await using var watch = fixture.Watch();
        Assert.True(await watch.NextAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var next = watch.NextAsync();

        watch.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var scansWhenCancelled = fixture.ScanCalls;
        Directory.Delete(fixture.Target, recursive: true);
        Directory.CreateDirectory(fixture.Target);
        await File.WriteAllTextAsync(Path.Combine(fixture.Target, "after-cancellation.mod"), "已取消", TestContext.Current.CancellationToken);
        await Task.Delay(180, TestContext.Current.CancellationToken);

        Assert.Equal(scansWhenCancelled, fixture.ScanCalls);
    }

    private sealed class Fixture : IGameLocator, IModScanner, IDisposable
    {
        private int _discoveryCalls;
        private int _scanCalls;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "paradox-mod-watch-tests-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "selected-game", "workshop", "mod");
        public int DiscoveryCalls => Volatile.Read(ref _discoveryCalls);
        public int ScanCalls => Volatile.Read(ref _scanCalls);

        public Fixture(bool createTarget = true)
        {
            Directory.CreateDirectory(Root);
            if (createTarget) Directory.CreateDirectory(Target);
        }

        public WatchSession Watch() => new(new FileSystemLocalModMonitor(this, this, TimeSpan.FromMilliseconds(20)), TestContext.Current.CancellationToken);

        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _discoveryCalls);
            return Task.FromResult(new GameEnvironment(null, Root, null, [Target], []) { LocalModWatchPaths = [Target] });
        }

        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.Exists(Target) ? Directory.GetFiles(Target, "*.mod", SearchOption.AllDirectories) : [];
            var mods = files.Order(StringComparer.Ordinal).Select(path =>
                new InstalledMod(Path.GetRelativePath(Target, path), File.ReadAllText(path), path, path,
                    null, null, null, [], ModOrigin.Local, File.Exists(path))).ToArray();
            var snapshot = new ModScanResult(mods, []);
            Interlocked.Increment(ref _scanCalls);
            return Task.FromResult(snapshot);
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class WatchSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly IAsyncEnumerator<ModScanResult> _snapshots;
        private Task<bool>? _pending;
        public ModScanResult Current => _snapshots.Current;

        public WatchSession(ILocalModMonitor monitor, CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _snapshots = monitor.WatchAsync(new(), _cancellation.Token).GetAsyncEnumerator();
        }

        public Task<bool> NextAsync() => _pending = _snapshots.MoveNextAsync().AsTask();
        public void Cancel() => _cancellation.Cancel();

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            try
            {
                if (_pending is not null)
                    try { await _pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                await _snapshots.DisposeAsync();
            }
            finally { _cancellation.Dispose(); }
        }
    }
}
