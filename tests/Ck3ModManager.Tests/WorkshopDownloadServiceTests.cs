using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkshopDownloadServiceTests
{
    private static WorkshopItem Item(ulong id = 101, uint appId = Ck3.AppId) =>
        new(id, appId, "测试 Mod", "", null, new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"), DateTimeOffset.UtcNow, 4, [], null);

    [Fact]
    public async Task ConcurrentClicksShareOneTaskUntilInstallationAndRealProgressComplete()
    {
        var provider = new Provider(); var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var changes = new List<WorkshopDownloadSnapshot>();
        service.Changed += changes.Add;
        var installed = 0;
        service.Installed += _ => installed++;
        var tasks = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => service.Start(Item(), "test-user"))));
        Assert.All(tasks, task => Assert.Same(tasks[0], task));
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.ResolveCalls);
        provider.Release.TrySetResult();
        await tasks[0].Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.DownloadCalls);
        Assert.Equal(1, installed);
        Assert.Equal(WorkshopDownloadState.Completed, tasks[0].Snapshot.State);
        Assert.Single(changes, snapshot => snapshot.State == WorkshopDownloadState.Downloading && snapshot.Bytes == 2 && snapshot.TotalBytes == 4);
        Assert.True(changes.FindIndex(snapshot => snapshot.State == WorkshopDownloadState.Verifying) < changes.FindIndex(snapshot => snapshot.State == WorkshopDownloadState.Installing));
        Assert.DoesNotContain(store.Saved, snapshot => snapshot.AuthenticationUrl is not null);
    }

    [Fact]
    public async Task SessionEvictsOldTerminalTasksWithoutEvictingActiveTasksOrReplayingOldSnapshots()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new Provider { Fail = true };
        provider.Release.TrySetResult();
        var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var first = service.Start(Item(), "user");
        await first.Completion.WaitAsync(token);
        for (ulong i = 0; i < WorkshopDownloadHistory.RecentLimit; i++)
            await service.Start(Item(200 + i), "user").Completion.WaitAsync(token);

        Assert.Equal(WorkshopDownloadHistory.RecentLimit, service.Tasks.Count);
        Assert.False(service.IsCurrent(first.Snapshot));
        Assert.Contains(store.Saved, entry => entry.TaskId == first.Snapshot.TaskId && entry.IsTerminal);

        store.FinalWriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var newest = service.Start(Item(400), "user");
        await store.FinalWriteStarted.Task.WaitAsync(token);
        Assert.Same(newest, service.Start(Item(400), "user"));
        Assert.True(service.IsCurrent(newest.Snapshot));
        Assert.Equal(WorkshopDownloadHistory.RecentLimit + 1, service.Tasks.Count);
        store.FinalWriteRelease.TrySetResult();
        await newest.Completion.WaitAsync(token);
        Assert.Equal(WorkshopDownloadHistory.RecentLimit, service.Tasks.Count);
        Assert.True(service.IsCurrent(newest.Snapshot));
    }

    [Fact]
    public async Task CancellingQueuedTaskDoesNotStartSecondTransferAndAllowsRetryAfterExit()
    {
        var provider = new Provider(); var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var first = service.Start(Item(), "user");
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.Start(Item(202), "user");
        second.Cancel();
        await second.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Cancelled, second.Snapshot.State);
        Assert.Equal(1, provider.ResolveCalls);
        first.Cancel();
        await first.Completion.WaitAsync(TestContext.Current.CancellationToken);
        provider.Release.TrySetResult();
        var retry = service.Start(Item(), "user");
        Assert.NotEqual(first.Snapshot.TaskId, retry.Snapshot.TaskId);
        await retry.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Completed, retry.Snapshot.State);
    }

    [Fact]
    public async Task InstalledMatchingRevisionIsVerifiedAndReusedWithoutTransfer()
    {
        var provider = new Provider(); provider.Release.TrySetResult();
        var store = new Store { Existing = new("existing-path", "existing.mod", true) };
        using var service = new WorkshopDownloadService(provider, store);
        var task = service.Start(Item(), "user");
        await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, provider.DownloadCalls);
        Assert.Equal("existing-path", task.Snapshot.InstalledPath);
        Assert.Equal(WorkshopDownloadState.Completed, task.Snapshot.State);
    }

    [Fact]
    public async Task MismatchingGameFromProviderFailsBeforeAnyContentIsInstalled()
    {
        var provider = new Provider { WrongGame = true }; provider.Release.TrySetResult();
        var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var task = service.Start(Item(), "user");
        await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Failed, task.Snapshot.State);
        Assert.Equal(0, store.InstallCalls);
        Assert.Equal(0, provider.DownloadCalls);
    }

    [Fact]
    public async Task RemovingActiveTaskAutomaticallyCancelsAndWaitsForFinalHistoryWrite()
    {
        var provider = new Provider();
        var store = new Store { FinalWriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new WorkshopDownloadService(provider, store);
        var task = service.Start(Item(), "user");
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var removal = service.RemoveAsync(task.Snapshot, TestContext.Current.CancellationToken);
        await store.FinalWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(removal.IsCompleted);
        Assert.Equal(WorkshopDownloadState.Cancelled, task.Snapshot.State);
        Assert.Equal(0, provider.DownloadCalls);
        store.FinalWriteRelease.TrySetResult();
        await removal;
        Assert.Empty(service.Tasks);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task CompletedTaskReleasesItsCancellationHandle()
    {
        var provider = new Provider { OpenCancellationHandle = true };
        using var service = new WorkshopDownloadService(provider, new Store());
        var task = service.Start(Item(), "user");
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var handle = Assert.IsAssignableFrom<WaitHandle>(provider.CancellationHandle).SafeWaitHandle;
        Assert.False(handle.IsClosed);
        task.Cancel();
        await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(handle.IsClosed);
        task.Cancel();
    }

    [Fact]
    public async Task HistoryReadThatOverlapsRemovalCannotRestoreTheRemovedTask()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new Provider();
        provider.Release.TrySetResult();
        var store = new Store { HistoryReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new WorkshopDownloadService(provider, store);
        var task = service.Start(Item(), "user");
        await task.Completion.WaitAsync(token);
        var history = service.ReadHistoryAsync(token);
        await store.HistoryCaptured.Task.WaitAsync(token);
        await service.RemoveAsync(task.Snapshot, token);
        store.HistoryReadRelease.TrySetResult();

        Assert.Empty(await history.WaitAsync(token));
        Assert.False(service.IsCurrent(task.Snapshot));
        Assert.False(service.HasHistory(task.Snapshot));
        Assert.Throws<InvalidOperationException>(() => service.Retry(task.Snapshot, Item(), "user"));
    }

    [Fact]
    public async Task RemovingQueuedTaskStopsItBeforeAnyProviderRequest()
    {
        var provider = new Provider(); var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var first = service.Start(Item(), "user");
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queued = service.Start(Item(202), "user");
        await service.RemoveAsync(queued.Snapshot, TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Cancelled, queued.Snapshot.State);
        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(0, provider.DownloadCalls);
        Assert.Same(first, Assert.Single(service.Tasks));
        first.Cancel();
        await first.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompletedInstallationStillNotifiesLibraryWhenTaskHistoryCannotBeWritten()
    {
        var provider = new Provider(); provider.Release.TrySetResult();
        var store = new Store { FailCompletedSave = true };
        using var service = new WorkshopDownloadService(provider, store);
        var installed = 0;
        service.Installed += _ => installed++;
        var task = service.Start(Item(), "user");
        await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Completed, task.Snapshot.State);
        Assert.Equal(1, installed);
    }

    [Fact]
    public async Task FailedTransferIsNotPublishedAsInstalledAndRetryStartsOneNewAttempt()
    {
        var provider = new Provider { Fail = true }; provider.Release.TrySetResult();
        var store = new Store();
        using var service = new WorkshopDownloadService(provider, store);
        var task = service.Start(Item(), "user");
        await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Failed, task.Snapshot.State);
        Assert.Equal(2, task.Snapshot.Bytes);
        Assert.Null(task.Snapshot.InstalledPath);
        Assert.Equal(0, store.InstallCalls);
        provider.Fail = false;
        var retry = service.Start(Item(), "user");
        await retry.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkshopDownloadState.Completed, retry.Snapshot.State);
        Assert.Equal(2, provider.DownloadCalls);
    }

    private sealed class Provider : IWorkshopContentProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResolveCalls; public int DownloadCalls; public bool WrongGame; public bool Fail;
        public bool OpenCancellationHandle;
        public WaitHandle? CancellationHandle;
        public async Task<WorkshopContentPlan> ResolveAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        {
            if (OpenCancellationHandle) CancellationHandle = cancellationToken.WaitHandle;
            Interlocked.Increment(ref ResolveCalls); Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(WrongGame ? 99U : item.ConsumerAppId, item.PublishedFileId, "revision-1", [new("descriptor.mod", 4, new string('0', 40))]);
        }
        public Task DownloadAsync(WorkshopContentPlan plan, string stagingDirectory, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DownloadCalls);
            progress.Report(new(WorkshopDownloadState.Downloading, 2, 4));
            progress.Report(new(WorkshopDownloadState.Downloading, 2, 4));
            if (Fail) throw new IOException("网络中断");
            progress.Report(new(WorkshopDownloadState.Downloading, 4, 4));
            return Task.CompletedTask;
        }
    }
    private sealed class Store : IWorkshopDownloadStore
    {
        public WorkshopInstallResult? Existing;
        public int InstallCalls;
        public bool FailCompletedSave;
        public TaskCompletionSource? FinalWriteRelease;
        public TaskCompletionSource? HistoryReadRelease;
        public TaskCompletionSource HistoryCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<WorkshopDownloadSnapshot> Saved { get; } = [];
        public Task<IDisposable> AcquireAsync(uint appId, ulong itemId, CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new MemoryStream());
        public string GetStagingDirectory(uint appId, ulong itemId, string revision) => "staging";
        public Task<WorkshopInstallResult?> FindVerifiedAsync(WorkshopContentPlan plan, string userDataDirectory, CancellationToken cancellationToken) => Task.FromResult(Existing);
        public Task<WorkshopInstallResult> InstallAsync(WorkshopContentPlan plan, string stagingDirectory, string userDataDirectory, CancellationToken cancellationToken)
        { InstallCalls++; return Task.FromResult(new WorkshopInstallResult("content", "descriptor", false)); }
        public async Task SaveTaskAsync(WorkshopDownloadSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (FailCompletedSave && snapshot.State == WorkshopDownloadState.Completed) throw new IOException("任务历史磁盘不可写");
            if (snapshot.IsTerminal && FinalWriteRelease is not null)
            { FinalWriteStarted.TrySetResult(); await FinalWriteRelease.Task.WaitAsync(cancellationToken); }
            lock (Saved) Saved.Add(snapshot);
        }
        public async Task<IReadOnlyList<WorkshopDownloadSnapshot>> ReadTasksAsync(CancellationToken cancellationToken)
        {
            WorkshopDownloadSnapshot[] snapshots;
            lock (Saved) snapshots = Saved.Where(snapshot => snapshot.IsTerminal).ToArray();
            HistoryCaptured.TrySetResult();
            if (HistoryReadRelease is not null) await HistoryReadRelease.Task.WaitAsync(cancellationToken);
            return snapshots;
        }
        public Task DeleteTaskAsync(Guid taskId, uint appId, CancellationToken cancellationToken)
        { lock (Saved) Saved.RemoveAll(snapshot => snapshot.TaskId == taskId && snapshot.AppId == appId); return Task.CompletedTask; }
    }
}
