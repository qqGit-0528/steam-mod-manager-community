using Avalonia.Headless.XUnit;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop.Tests;

public sealed class SteamClientDownloadsUiTests
{
    [AvaloniaFact]
    public void NativeRowShowsSteamCountersAndInstallationSizeWithoutQr()
    {
        var snapshot = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "native", WorkshopDownloadState.Downloading,
            40, 100, DateTimeOffset.UtcNow, AuthenticationUrl: "https://s.team/q/1/unused") { Owner = WorkshopDownloadOwner.SteamClient };
        using var row = new DownloadRow(snapshot, _ => { }, _ => { });
        Assert.False(row.HasQrCode); Assert.Equal(40, row.Percent);
        Assert.Equal("停止下载", row.CancelText);
        Assert.Contains("Steam 客户端可能仍会继续下载", row.CancelTooltip);
        Assert.Contains("由 Steam 管理", row.RemoveTooltip);
        row.Update(snapshot with { State = WorkshopDownloadState.Completed, InstalledPath = "/steam/content/101", InstalledSizeBytes = 500,
            UpdatedAt = snapshot.UpdatedAt.AddSeconds(1) });
        Assert.Equal(100, row.Percent); Assert.Equal("安装时大小 · 500 B", row.ProgressText);
        Assert.Equal("下载完成", row.StateText);
        Assert.False(row.IsIndeterminate); Assert.False(row.HasInstalledPath);
        row.UpdateInstallation(new(LocalInstallationState.Installed, "/steam/content/101"));
        Assert.Equal("当前已安装", row.InstallationStatus);
        Assert.True(row.OpenDirectoryCommand.CanExecute(null));
        Assert.False(row.HasQrCode);
    }

    [AvaloniaFact]
    public async Task NativeServiceDeduplicatesAndPersistsRealSteamPathWithoutCopyingContent()
    {
        var root = Directory.CreateTempSubdirectory("native-service-").FullName;
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root, "steam", "101")).FullName;
            await File.WriteAllTextAsync(Path.Combine(content, "descriptor.mod"), "name=\"native\"", TestContext.Current.CancellationToken);
            var provider = new NativeProvider(content);
            var store = new FileWorkshopDownloadStore(Path.Combine(root, "app"));
            using var service = new WorkshopDownloadService(provider, store);
            var changes = new List<WorkshopDownloadSnapshot>(); service.Changed += changes.Add;
            var item = Item();
            var tasks = Enumerable.Range(0, 20).Select(_ => service.Start(item, root)).ToArray();
            Assert.All(tasks, t => Assert.Same(tasks[0], t));
            await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            provider.Release.TrySetResult();
            await tasks[0].Completion.WaitAsync(TestContext.Current.CancellationToken);
            var snapshot = tasks[0].Snapshot;
            Assert.Equal(WorkshopDownloadState.Completed, snapshot.State);
            Assert.Equal(WorkshopDownloadOwner.SteamClient, snapshot.Owner);
            Assert.Equal(content, snapshot.InstalledPath);
            Assert.Equal(500, snapshot.InstalledSizeBytes);
            Assert.Equal(40, snapshot.Bytes); Assert.Equal(100, snapshot.TotalBytes);
            Assert.Contains(changes, s => s.State == WorkshopDownloadState.Downloading && s.Bytes == 40);
            Assert.Equal(snapshot, Assert.Single(await store.ReadTasksAsync(TestContext.Current.CancellationToken)));
            await service.RemoveAsync(snapshot, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(content, "descriptor.mod")));
            Assert.Empty(await store.ReadTasksAsync(TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(root, "app", "revisions")));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task RemovingNativeActiveTaskStopsTrackingAndKeepsSteamContent()
    {
        var root = Directory.CreateTempSubdirectory("native-stop-").FullName;
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root, "steam", "101")).FullName;
            var descriptor = Path.Combine(content, "descriptor.mod");
            var provider = new NativeProvider(content);
            var store = new FileWorkshopDownloadStore(Path.Combine(root, "app"));
            using var service = new WorkshopDownloadService(provider, store);
            var task = service.Start(Item(), root);
            await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            task.Cancel();
            await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
            // Steam 仍可在应用停止跟踪后安装内容，旧任务移除必须保留它。
            await File.WriteAllTextAsync(descriptor, "name=native", TestContext.Current.CancellationToken);
            await service.RemoveAsync(task.Snapshot, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(descriptor));
            Assert.Equal(WorkshopDownloadState.Interrupted, task.Snapshot.State);
            Assert.Contains("Steam 后台下载不受影响", task.Snapshot.Message);
            Assert.Empty(service.Tasks);
            Assert.Empty(await store.ReadTasksAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task RemovingCompletedNativeTaskKeepsSteamContent()
    {
        var root = Directory.CreateTempSubdirectory("native-keep-").FullName;
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root, "steam", "101")).FullName;
            await File.WriteAllTextAsync(Path.Combine(content, "descriptor.mod"), "name=\"native\"", TestContext.Current.CancellationToken);
            var provider = new NativeProvider(content);
            var store = new FileWorkshopDownloadStore(Path.Combine(root, "app"));
            using var service = new WorkshopDownloadService(provider, store);
            var task = service.Start(Item(), root);
            await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            provider.Release.TrySetResult();
            await task.Completion.WaitAsync(TestContext.Current.CancellationToken);
            await service.RemoveAsync(task.Snapshot, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(content, "descriptor.mod")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static WorkshopItem Item() => new(101, Ck3.AppId, "native", "", null,
        new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=101"), DateTimeOffset.UtcNow, 4, [], null);

    [AvaloniaFact]
    public async Task NativeTaskReusesLocalIdentityOutsideTheHistoryWindowAfterRestart()
    {
        var root = Directory.CreateTempSubdirectory("native-history-identity-").FullName;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new FileWorkshopDownloadStore(root);
            var old = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "旧任务",
                WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow.AddDays(-1))
            { Owner = WorkshopDownloadOwner.SteamClient };
            // 模拟升级前保存的 GUID 文件。
            var appHistory = Directory.CreateDirectory(Path.Combine(root, "downloads", Ck3.AppId.ToString()));
            var original = Path.Combine(appHistory.FullName, old.TaskId.ToString("N") + ".json");
            await File.WriteAllTextAsync(original, System.Text.Json.JsonSerializer.Serialize(old), token);
            var other = old with { TaskId = Guid.NewGuid(), AppId = GameCatalog.Victoria3.AppId, UpdatedAt = DateTimeOffset.UtcNow };
            await store.SaveTaskAsync(other, token);
            for (var i = 0; i < WorkshopDownloadHistory.RecentLimit + 1; i++)
                await store.SaveTaskAsync(old with { TaskId = Guid.NewGuid(), PublishedFileId = (ulong)(200 + i),
                    UpdatedAt = DateTimeOffset.UtcNow }, token);

            var provider = new NativeProvider(root);
            using var restarted = new WorkshopDownloadService(provider, new FileWorkshopDownloadStore(root));
            Assert.DoesNotContain(await restarted.ReadHistoryAsync(token), entry => entry.TaskId == old.TaskId);
            var task = restarted.Start(Item(), root);
            Assert.Same(task, restarted.Start(Item(), root));
            await task.IdentityReady.WaitAsync(token);
            Assert.Equal(old.TaskId, task.Snapshot.TaskId);
            provider.Release.TrySetResult();
            await task.Completion.WaitAsync(token);
            Assert.Equal(WorkshopDownloadState.Completed, task.Snapshot.State);
            Assert.Equal(old.TaskId, Assert.Single(await store.ReadTasksAsync(token), entry => entry.AppId == Ck3.AppId &&
                entry.PublishedFileId == 101).TaskId);
            Assert.Equal(WorkshopDownloadHistory.RecentLimit + 2, appHistory.GetFiles("*.json").Length);
            Assert.True(File.Exists(original));
            Assert.Equal(other.TaskId, (await store.FindSteamItemTaskAsync(other.AppId, 101, null, token))!.TaskId);
            using var withoutHistory = new WorkshopDownloadService(provider, new FileWorkshopDownloadStore(root));
            Assert.False(withoutHistory.HasHistory(task.Snapshot));
            Assert.False(await withoutHistory.RemoveAsync(old, token));
            Assert.True(await withoutHistory.RemoveAsync(task.Snapshot, token));
            Assert.False(File.Exists(original));
            Assert.Equal(other.TaskId, (await store.FindSteamItemTaskAsync(other.AppId, 101, null, token))!.TaskId);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task LockConflictCannotOverwriteOrRemoveAnotherInstancesHistory()
    {
        var root = Directory.CreateTempSubdirectory("native-lock-").FullName;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new FileWorkshopDownloadStore(root);
            var activeProvider = new NativeProvider(root);
            using var first = new WorkshopDownloadService(activeProvider, store);
            var active = first.Start(Item(), root);
            await activeProvider.Started.Task.WaitAsync(token);

            var otherProvider = new NativeProvider(root);
            using var other = new WorkshopDownloadService(otherProvider, store);
            Assert.Equal(active.Snapshot.TaskId, Assert.Single(await other.ReadHistoryAsync(token)).TaskId);
            var conflict = other.Start(Item(), root);
            await conflict.Completion.WaitAsync(token);
            Assert.Equal(WorkshopDownloadState.Failed, conflict.Snapshot.State);
            Assert.False(otherProvider.Started.Task.IsCompleted);
            Assert.Equal(WorkshopDownloadState.Interrupted, Assert.Single(await store.ReadTasksAsync(token)).State);

            activeProvider.Release.TrySetResult();
            await active.Completion.WaitAsync(token);
            await other.RemoveAsync(conflict.Snapshot, token);
            await other.RemoveAsync(conflict.Snapshot, token);
            Assert.Equal(active.Snapshot, Assert.Single(await store.ReadTasksAsync(token)));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task RemovalBlocksNewAttemptUntilDeleteFinishesThenUsesFreshIdentity()
    {
        var root = Directory.CreateTempSubdirectory("native-removal-race-").FullName;
        try
        {
            var provider = new NativeProvider(root); provider.Release.TrySetResult();
            var store = new HoldingDeleteStore(new FileWorkshopDownloadStore(Path.Combine(root, "app")));
            using var service = new WorkshopDownloadService(provider, store);
            var first = service.Start(Item(), root); await first.Completion.WaitAsync(TestContext.Current.CancellationToken);
            var removal = service.RemoveAsync(first.Snapshot, TestContext.Current.CancellationToken);
            await store.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => service.Start(Item(), root));
            store.Release.TrySetResult(); await removal;
            Assert.Empty(service.Tasks);
            Assert.Throws<InvalidOperationException>(() => service.Retry(first.Snapshot, Item(), root));
            var next = service.Start(Item(), root); await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(first.Snapshot.TaskId, next.Snapshot.TaskId);
            Assert.Same(next, Assert.Single(service.Tasks));
            Assert.Equal(next.Snapshot.TaskId, Assert.Single(await store.ReadTasksAsync(TestContext.Current.CancellationToken)).TaskId);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task RemovalAlsoDeletesDuplicatesThatCouldNotBeMergedWhileAnotherInstanceHeldLock()
    {
        var root = Directory.CreateTempSubdirectory("native-duplicate-lock-").FullName;
        try
        {
            var store = new FileWorkshopDownloadStore(root);
            var first = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "native", WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow)
                { Owner = WorkshopDownloadOwner.SteamClient };
            await store.SaveTaskAsync(first, TestContext.Current.CancellationToken);
            await store.SaveTaskAsync(first with { TaskId = Guid.NewGuid(), UpdatedAt = first.UpdatedAt.AddSeconds(-1) }, TestContext.Current.CancellationToken);
            using var service = new WorkshopDownloadService(new NativeProvider(root), store);
            IReadOnlyList<WorkshopDownloadSnapshot> visible;
            using (await store.AcquireAsync(Ck3.AppId, 101, TestContext.Current.CancellationToken))
            {
                visible = await service.ReadHistoryAsync(TestContext.Current.CancellationToken);
                Assert.Single(visible); Assert.Equal(2, (await store.ReadTasksAsync(TestContext.Current.CancellationToken)).Count);
            }
            await service.RemoveAsync(visible[0], TestContext.Current.CancellationToken);
            Assert.Empty(await store.ReadTasksAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await service.ReadHistoryAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HoldingDeleteStore(IWorkshopDownloadStore inner) : IWorkshopDownloadStore, ISteamClientTaskHistoryStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDisposable> AcquireAsync(uint appId, ulong itemId, CancellationToken token) => inner.AcquireAsync(appId, itemId, token);
        public string GetStagingDirectory(uint appId, ulong itemId, string revision) => inner.GetStagingDirectory(appId, itemId, revision);
        public Task<WorkshopInstallResult?> FindVerifiedAsync(WorkshopContentPlan plan, string directory, CancellationToken token) => inner.FindVerifiedAsync(plan, directory, token);
        public Task<WorkshopInstallResult> InstallAsync(WorkshopContentPlan plan, string staging, string directory, CancellationToken token) => inner.InstallAsync(plan, staging, directory, token);
        public Task SaveTaskAsync(WorkshopDownloadSnapshot snapshot, CancellationToken token) => inner.SaveTaskAsync(snapshot, token);
        public Task<IReadOnlyList<WorkshopDownloadSnapshot>> ReadTasksAsync(CancellationToken token) => inner.ReadTasksAsync(token);
        public async Task DeleteTaskAsync(Guid id, uint appId, CancellationToken token)
        { Started.TrySetResult(); await Release.Task.WaitAsync(token); await inner.DeleteTaskAsync(id, appId, token); }
        public Task<WorkshopDownloadSnapshot?> FindSteamItemTaskAsync(uint appId, ulong itemId, Guid? knownTaskId, CancellationToken token) =>
            ((ISteamClientTaskHistoryStore)inner).FindSteamItemTaskAsync(appId, itemId, knownTaskId, token);
        public Task DeleteSteamItemTaskIfUnchangedAsync(WorkshopDownloadSnapshot snapshot, CancellationToken token) =>
            ((ISteamClientTaskHistoryStore)inner).DeleteSteamItemTaskIfUnchangedAsync(snapshot, token);
        public async Task<bool> DeleteSteamItemTasksAsync(uint appId, ulong itemId, CancellationToken token,
            Guid? taskId = null, DateTimeOffset? expectedUpdatedAt = null)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await ((ISteamClientTaskHistoryStore)inner).DeleteSteamItemTasksAsync(appId, itemId, token, taskId, expectedUpdatedAt);
        }
    }
    private sealed class NativeProvider(string path) : ISteamClientWorkshopDownloader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SteamClientInstallation> DownloadAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new(WorkshopDownloadState.Downloading, 40, 100, TransferredBytes: 40)); Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(path, 500, false, 40, 100, "steam-client:123");
        }
    }
}
