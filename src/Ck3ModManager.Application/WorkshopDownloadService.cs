using static Ck3ModManager.Core.AppText;
using System.Diagnostics;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed class WorkshopDownloadTask
{
    private readonly object _cancellationGate = new();
    private CancellationTokenSource? _cancellation = new();
    private Task? _cancellationCallbacks;
    private readonly TaskCompletionSource _identityReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkshopDownloadSnapshot _snapshot;
    internal WorkshopDownloadTask(WorkshopDownloadSnapshot snapshot) => _snapshot = snapshot;
    public WorkshopDownloadSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public Task IdentityReady => _identityReady.Task;
    internal CancellationToken Token => _cancellation!.Token;
    internal bool AcquiredItemLock { get; set; }
    internal void Set(WorkshopDownloadSnapshot value) => Volatile.Write(ref _snapshot, value);
    internal void ConfirmIdentity() => _identityReady.TrySetResult();
    public void Cancel()
    {
        lock (_cancellationGate)
            if (!Snapshot.IsTerminal && _cancellation is { } cancellation)
                _cancellationCallbacks ??= cancellation.CancelAsync();
    }

    internal async Task ReleaseCancellationAsync()
    {
        CancellationTokenSource? cancellation;
        Task? callbacks;
        lock (_cancellationGate)
        {
            cancellation = _cancellation;
            _cancellation = null;
            callbacks = _cancellationCallbacks;
            _cancellationCallbacks = null;
        }

        // 取消回调可能恢复任务本身，等通知结束后再释放句柄。
        try
        {
            if (callbacks is not null) await callbacks.ConfigureAwait(false);
        }
        finally { cancellation?.Dispose(); }
    }
}

/// <summary>原子创建任务；同一游戏工坊项从解析到安装始终只允许一个活跃任务。</summary>
public sealed class WorkshopDownloadService : IDisposable
{
    private readonly IWorkshopContentProvider? provider;
    private readonly ISteamClientWorkshopDownloader? steamClient;
    private readonly IWorkshopDownloadStore store;

    public WorkshopDownloadService(IWorkshopContentProvider provider, IWorkshopDownloadStore store)
    { this.provider = provider; this.store = store; }

    public WorkshopDownloadService(ISteamClientWorkshopDownloader steamClient, IWorkshopDownloadStore store)
    { this.steamClient = steamClient; this.store = store; }
    private readonly object _gate = new();
    private readonly Dictionary<(uint, ulong), WorkshopDownloadTask> _active = [];
    private readonly Dictionary<Guid, WorkshopDownloadTask> _tasks = [];
    private readonly LinkedList<Guid> _recentTasks = [];
    private readonly Dictionary<(uint, ulong), WorkshopDownloadSnapshot> _known = [];
    private readonly HashSet<(uint, ulong)> _removing = [];
    private readonly Dictionary<Guid, (uint, ulong)> _applicationHistory = [];
    private long _removalVersion;
    private readonly SemaphoreSlim _queue = new(1);
    private bool _disposed;
    public event Action<WorkshopDownloadSnapshot>? Changed;
    public event Action<WorkshopDownloadSnapshot>? Installed;
    public IReadOnlyList<WorkshopDownloadTask> Tasks { get { lock (_gate) return _tasks.Values.ToArray(); } }

    // 界面处理后台事件时，只使用仍存在且最新的快照。
    public bool IsCurrent(WorkshopDownloadSnapshot snapshot)
    {
        lock (_gate) return _tasks.TryGetValue(snapshot.TaskId, out var task) && ReferenceEquals(task.Snapshot, snapshot);
    }

    public bool HasHistory(WorkshopDownloadSnapshot snapshot)
    {
        lock (_gate) return HasHistoryCore(snapshot);
    }

    private bool HasHistoryCore(WorkshopDownloadSnapshot snapshot)
    {
        var key = (snapshot.AppId, snapshot.PublishedFileId);
        return snapshot.Owner == WorkshopDownloadOwner.SteamClient
            ? _known.GetValueOrDefault(key)?.TaskId == snapshot.TaskId
            : _applicationHistory.TryGetValue(snapshot.TaskId, out var item) && item == key;
    }

    public WorkshopDownloadTask Start(WorkshopItem item, string userDataDirectory) => StartCore(item, userDataDirectory, null);

    public WorkshopDownloadTask Retry(WorkshopDownloadSnapshot previous, WorkshopItem item, string userDataDirectory)
    {
        if (previous.AppId != item.ConsumerAppId || previous.PublishedFileId != item.PublishedFileId || previous.TaskId == Guid.Empty)
            throw new ArgumentException(T("TheTaskAndItemDoNotMatch"));
        return StartCore(item, userDataDirectory, previous);
    }

    private WorkshopDownloadTask StartCore(WorkshopItem item, string userDataDirectory, WorkshopDownloadSnapshot? previous)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PublishedFileId == 0 || item.ConsumerAppId == 0) throw new ArgumentException(T("TheDownloadItemIDIsInvalid"));
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var directory = Path.GetFullPath(userDataDirectory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (item.ConsumerAppId, item.PublishedFileId);
            if (_removing.Contains(key)) throw new InvalidOperationException(T("ThisModSTaskIsBeingRemoved"));
            if (previous is not null && !HasHistoryCore(previous) &&
                FindTask(previous) is null)
                throw new InvalidOperationException(T("ThisTaskWasRemovedSelectTheMod"));
            if (_active.TryGetValue(key, out var existing)) return existing;
            previous ??= steamClient is null ? null : _known.GetValueOrDefault(key);
            var id = previous?.TaskId ?? Guid.NewGuid();
            ForgetTask(id);
            if (steamClient is not null) ForgetRecentSteamTasks(key);
            var task = new WorkshopDownloadTask(new(id, key.ConsumerAppId, key.PublishedFileId,
                item.Title, WorkshopDownloadState.Queued, 0, null, DateTimeOffset.UtcNow)
                {
                    Owner = steamClient is null ? WorkshopDownloadOwner.Application : WorkshopDownloadOwner.SteamClient,
                    WorkshopGameVersions = WorkshopCatalog.EvaluateItem(item, null).GameVersions
                });
            _active.Add(key, task);
            _tasks.Add(id, task);
            task.Completion = Task.Run(() => RunAsync(task, item, directory, previous));
            return task;
        }
    }

    private WorkshopDownloadTask? FindTask(WorkshopDownloadSnapshot snapshot) =>
        _tasks.TryGetValue(snapshot.TaskId, out var task) && task.Snapshot.AppId == snapshot.AppId &&
        task.Snapshot.PublishedFileId == snapshot.PublishedFileId ? task : null;

    private void ForgetTask(Guid id)
    {
        _tasks.Remove(id);
        _recentTasks.Remove(id);
    }

    private void ForgetRecentSteamTasks((uint AppId, ulong PublishedFileId) key)
    {
        for (var node = _recentTasks.First; node is not null;)
        {
            var next = node.Next;
            var snapshot = _tasks[node.Value].Snapshot;
            if (snapshot.Owner == WorkshopDownloadOwner.SteamClient &&
                (snapshot.AppId, snapshot.PublishedFileId) == key)
            {
                _tasks.Remove(node.Value);
                _recentTasks.Remove(node);
            }
            node = next;
        }
    }

    private void RememberHistory((uint, ulong) key, WorkshopDownloadSnapshot snapshot)
    {
        _known[key] = snapshot;
        if (_known.Count > WorkshopDownloadHistory.RecentLimit)
            _known.Remove(_known.MinBy(entry => entry.Value.UpdatedAt).Key);
    }

    public async Task<IReadOnlyList<WorkshopDownloadSnapshot>> ReadHistoryAsync(CancellationToken token = default)
    {
        while (true)
        {
            long version;
            lock (_gate) version = _removalVersion;
            var history = await store.ReadTasksAsync(token).ConfigureAwait(false);
            var result = history.Where(s => s.Owner != WorkshopDownloadOwner.SteamClient).ToList();
            foreach (var group in history.Where(s => s.Owner == WorkshopDownloadOwner.SteamClient).GroupBy(s => (s.AppId, s.PublishedFileId)))
            {
                var ordered = group.OrderByDescending(s => s.UpdatedAt).ToArray();
                result.Add(ordered[0]);
                foreach (var duplicate in ordered.Skip(1))
                    try
                    {
                        if (store is ISteamClientTaskHistoryStore nativeHistory)
                            await nativeHistory.DeleteSteamItemTaskIfUnchangedAsync(duplicate, token).ConfigureAwait(false);
                        else await store.DeleteTaskAsync(duplicate.TaskId, duplicate.AppId, token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { /* 暂时无法清理的旧记录仍合并显示。 */ }
            }
            lock (_gate)
            {
                // 读取跨越一次移除时重新加载，避免持有已经删除的历史快照。
                if (version != _removalVersion) continue;
                _applicationHistory.Clear();
                result.RemoveAll(snapshot => _removing.Contains((snapshot.AppId, snapshot.PublishedFileId)));
                var visible = result.OrderByDescending(s => s.UpdatedAt).Take(WorkshopDownloadHistory.RecentLimit).ToArray();
                foreach (var snapshot in visible)
                {
                    var key = (snapshot.AppId, snapshot.PublishedFileId);
                    if (snapshot.Owner != WorkshopDownloadOwner.SteamClient)
                        _applicationHistory[snapshot.TaskId] = key;
                    else if (!_active.ContainsKey(key) && (_known.GetValueOrDefault(key) is not { } known || known.UpdatedAt <= snapshot.UpdatedAt))
                        RememberHistory(key, snapshot);
                }
                return visible;
            }
        }
    }

    public async Task<bool> RemoveAsync(WorkshopDownloadSnapshot snapshot, CancellationToken token = default)
    {
        WorkshopDownloadTask? task;
        bool knownHistory;
        var key = (snapshot.AppId, snapshot.PublishedFileId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            task = FindTask(snapshot);
            knownHistory = task is not null || HasHistoryCore(snapshot);
            if (task is null)
            {
                if (!knownHistory && (snapshot.Owner != WorkshopDownloadOwner.SteamClient || store is not ISteamClientTaskHistoryStore))
                    return false;
                if (!snapshot.IsTerminal || _active.ContainsKey(key))
                    throw new InvalidOperationException(T("ThisModIsAlreadyBeingProcessedStop"));
            }
            if (!_removing.Add(key)) throw new InvalidOperationException(T("ThisModSTaskIsBeingRemoved2"));
        }
        // 先停止网络与排队等待，再等终态历史写入结束；原子安装提交不会被中途打断。
        try
        {
            task?.Cancel();
            if (task is not null) await task.Completion.WaitAsync(token).ConfigureAwait(false);
            var target = task?.Snapshot ?? snapshot;
            var grouped = target.Owner == WorkshopDownloadOwner.SteamClient ? store as ISteamClientTaskHistoryStore : null;
            // 未获得物品锁的本地尝试没有写入历史，移除它时不能删除另一实例的记录。
            if (task is null || task.AcquiredItemLock)
            {
                if (grouped is not null)
                {
                    // 卡片可能比内存历史保留得更久。仍需验证它对应的是磁盘上的同一条记录。
                    if (!await grouped.DeleteSteamItemTasksAsync(target.AppId, target.PublishedFileId, token,
                        target.TaskId, knownHistory ? null : target.UpdatedAt).ConfigureAwait(false)) return false;
                }
                else await store.DeleteTaskAsync(target.TaskId, target.AppId, token).ConfigureAwait(false);
            }
            lock (_gate)
            {
                ForgetTask(snapshot.TaskId);
                if (grouped is not null) ForgetRecentSteamTasks(key);
                if (_known.GetValueOrDefault(key) is { } known && (grouped is not null || known.TaskId == snapshot.TaskId))
                    _known.Remove(key);
                _applicationHistory.Remove(snapshot.TaskId);
                _removalVersion++;
            }
            return true;
        }
        finally { lock (_gate) _removing.Remove(key); }
    }

    private async Task RunAsync(WorkshopDownloadTask task, WorkshopItem item, string directory, WorkshopDownloadSnapshot? previous)
    {
        var token = task.Token;
        var entered = false;
        IDisposable? processLock = null;
        var clock = Stopwatch.StartNew();
        var lastBytes = 0L;
        var lastSample = TimeSpan.Zero;
        void Update(WorkshopTransferProgress update)
        {
            if (task.Snapshot.IsTerminal) return;
            if (steamClient is not null && (update.AuthenticationUrl is not null || update.State == WorkshopDownloadState.AwaitingAuthentication))
                throw new InvalidDataException(T("SteamClientDownloadsUseYourExistingSteam"));
            var current = task.Snapshot;
            var now = clock.Elapsed;
            var speed = update.State == WorkshopDownloadState.Downloading && now - lastSample >= TimeSpan.FromMilliseconds(250)
                ? Math.Max(0, update.TransferredBytes - lastBytes) / Math.Max(0.001, (now - lastSample).TotalSeconds) : current.BytesPerSecond;
            if (now - lastSample >= TimeSpan.FromMilliseconds(250)) { lastSample = now; lastBytes = update.TransferredBytes; }
            var next = current with { State = update.State, Bytes = update.Bytes, TotalBytes = update.TotalBytes,
                Message = update.Message, AuthenticationUrl = update.AuthenticationUrl, BytesPerSecond = speed };
            if (next == current) return;
            next = next with { UpdatedAt = DateTimeOffset.UtcNow };
            task.Set(next);
            Publish(next);
        }
        var progress = new InlineProgress<WorkshopTransferProgress>(Update);
        try
        {
            // 排队期间也持有物品锁；保留到终态历史写完，供其他实例判断记录是否仍在使用。
            processLock = await store.AcquireAsync(item.ConsumerAppId, item.PublishedFileId, token).ConfigureAwait(false);
            if (steamClient is not null && store is ISteamClientTaskHistoryStore history)
            {
                var existing = await history.FindSteamItemTaskAsync(item.ConsumerAppId, item.PublishedFileId,
                    previous?.TaskId, token).ConfigureAwait(false);
                var resolvedId = existing?.TaskId ?? (previous is null ? task.Snapshot.TaskId : Guid.NewGuid());
                lock (_gate)
                {
                    var snapshot = task.Snapshot;
                    if (resolvedId != snapshot.TaskId)
                    {
                        ForgetTask(snapshot.TaskId);
                        ForgetTask(resolvedId);
                        task.Set(snapshot with { TaskId = resolvedId });
                        _tasks.Add(resolvedId, task);
                    }
                }
                previous = existing;
            }
            task.AcquiredItemLock = true;
            task.ConfirmIdentity();
            await store.SaveTaskAsync(task.Snapshot, token).ConfigureAwait(false);
            await _queue.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            if (steamClient is not null)
            {
                Update(new(WorkshopDownloadState.Resolving, Message: T("ConnectingToTheLocalSteamClient")));
                var result = await steamClient.DownloadAsync(item, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if ((!Directory.Exists(result.ContentPath) && !File.Exists(result.ContentPath)) || result.SizeOnDisk < 0)
                    throw new InvalidDataException(T("TheInstallationReturnedBySteamIsUnavailable"));
                var unchanged = result.Reused && previous?.State == WorkshopDownloadState.Completed &&
                    previous.Revision == result.Revision && previous.InstalledPath == result.ContentPath;
                task.Set(task.Snapshot with { InstalledPath = result.ContentPath, InstalledSizeBytes = result.SizeOnDisk, Revision = result.Revision, IsUpToDate = unchanged });
                // 监狱建筑师的旧版工坊内容由游戏自己读取，下载后仍需在游戏内启用。
                var needsManualActivation = GameCatalog.FindByAppId(item.ConsumerAppId)?.Id == GameCatalog.PrisonArchitect.Id;
                Update(new(WorkshopDownloadState.Completed, result.DownloadedBytes, result.DownloadTotalBytes,
                    unchanged ? T("AlreadyUpToDate")
                        : needsManualActivation ? T("DownloadedEnableItFromTheGameS")
                        : result.Reused ? T("ReusingTheSteamInstallation") : T("SteamFinishedDownloadingAndInstallingTheMod")));
                if (!unchanged) Notify(Installed, task.Snapshot);
                return;
            }
            Update(new(WorkshopDownloadState.Resolving, Message: T("CheckingTheCurrentSteamContent")));
            var plan = await provider!.ResolveAsync(item, progress, token).ConfigureAwait(false);
            if (plan.AppId != item.ConsumerAppId || plan.PublishedFileId != item.PublishedFileId || string.IsNullOrWhiteSpace(plan.Revision))
                throw new InvalidDataException(T("TheSourceReturnedADifferentItemOr"));
            task.Set(task.Snapshot with { Revision = plan.Revision });
            await store.SaveTaskAsync(task.Snapshot, token).ConfigureAwait(false);
            Update(new(WorkshopDownloadState.Verifying, TotalBytes: plan.TotalBytes, Message: T("CheckingExistingFiles")));
            var installed = await store.FindVerifiedAsync(plan, directory, token).ConfigureAwait(false);
            if (installed is null)
            {
                var staging = store.GetStagingDirectory(plan.AppId, plan.PublishedFileId, plan.Revision);
                await provider.DownloadAsync(plan, staging, progress, token).ConfigureAwait(false);
                Update(new(WorkshopDownloadState.Verifying, plan.TotalBytes, plan.TotalBytes, T("VerifyingAllFiles")));
                token.ThrowIfCancellationRequested();
                Update(new(WorkshopDownloadState.Installing, plan.TotalBytes, plan.TotalBytes, T("InstallingToTheLocalLibrary")));
                // 安装提交不接受中途取消；旧修订和配置引用始终保留。
                installed = await store.InstallAsync(plan, staging, directory, CancellationToken.None).ConfigureAwait(false);
            }
            task.Set(task.Snapshot with { InstalledPath = installed.ContentPath });
            // 再核对一次来源；作者在下载期间发布新内容时明确提示，保留本次一致修订。
            var message = installed.Reused ? T("ExistingFilesPassedVerification") : T("DownloadCompleteFilesVerifiedAndInstalled");
            try
            {
                var latest = await provider.ResolveAsync(item, new InlineProgress<WorkshopTransferProgress>(_ => { }), token).ConfigureAwait(false);
                if (latest.Revision != plan.Revision) message += T("AnUpdateIsAvailableOnSteamSelect");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException) { message += T("SteamUpdateStatusIsTemporarilyUnavailable"); }
            Update(new(WorkshopDownloadState.Completed, plan.TotalBytes, plan.TotalBytes, message));
            // 文件提交后的通知不依赖任务历史是否可写。
            Notify(Installed, task.Snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update(steamClient is null
                ? new(WorkshopDownloadState.Cancelled, task.Snapshot.Bytes, task.Snapshot.TotalBytes, T("DownloadCanceledVerifiedContentIsAvailableFor"))
                : new(WorkshopDownloadState.Interrupted, task.Snapshot.Bytes, task.Snapshot.TotalBytes, T("TrackingStoppedSteamCanContinueDownloadingIn")));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Update(new(WorkshopDownloadState.Failed, task.Snapshot.Bytes, task.Snapshot.TotalBytes, error.Message)); }
        finally
        {
            task.ConfirmIdentity();
            if (processLock is not null)
            {
                try
                {
                    if (task.AcquiredItemLock)
                        await store.SaveTaskAsync(task.Snapshot with { AuthenticationUrl = null }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { /* 内容结果保留，历史记录不可写不删除文件。 */ }
                finally { processLock.Dispose(); }
            }
            if (entered) _queue.Release();
            try { await task.ReleaseCancellationAsync().ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    var key = (item.ConsumerAppId, item.PublishedFileId);
                    if (steamClient is not null && task.AcquiredItemLock) RememberHistory(key, task.Snapshot);
                    _active.Remove(key);
                    _recentTasks.AddLast(task.Snapshot.TaskId);
                    if (_recentTasks.Count > WorkshopDownloadHistory.RecentLimit)
                    {
                        ForgetTask(_recentTasks.MinBy(id => _tasks[id].Snapshot.UpdatedAt));
                    }
                }
            }
        }
    }

    private void Publish(WorkshopDownloadSnapshot snapshot) => Notify(Changed, snapshot);
    private static void Notify(Action<WorkshopDownloadSnapshot>? handlers, WorkshopDownloadSnapshot snapshot)
    {
        if (handlers is null) return;
        foreach (Action<WorkshopDownloadSnapshot> handler in handlers.GetInvocationList())
            try { handler(snapshot); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void Dispose()
    {
        WorkshopDownloadTask[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active.Values.ToArray();
        }
        foreach (var task in active) task.Cancel();
    }
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
