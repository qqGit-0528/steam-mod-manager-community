using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>下载目录由本工具独占；只发布通过完整校验的不可变修订，不写 Steam 工坊目录。</summary>
public sealed class FileWorkshopDownloadStore(string rootDirectory) : IWorkshopDownloadStore, ISteamClientTaskHistoryStore
{
    private readonly string _root = Path.GetFullPath(rootDirectory);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const string OrderedTaskSuffix = ".ordered.json";
    private LegacyHistory? _legacyHistory;

    private sealed record LegacyHistory(string Stamp, WorkshopDownloadSnapshot[] Recent);
    private string ItemRoot(uint appId, ulong id) => Path.Combine(_root, "content", "steam", appId.ToString(), id.ToString());
    private static string RevisionKey(string revision) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision))).ToLowerInvariant();
    private string RevisionDirectory(WorkshopContentPlan plan) => Path.Combine(ItemRoot(plan.AppId, plan.PublishedFileId), "revisions", RevisionKey(plan.Revision));
    private static string Wrapper(WorkshopContentPlan plan, string userDirectory) => Path.Combine(Path.GetFullPath(userDirectory), "mod", $"app_{plan.AppId}_{plan.PublishedFileId}.mod");

    internal bool IsManagedRevision(uint appId, ulong itemId, string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length != 64 || !name.All(char.IsAsciiHexDigit) ||
            !SteamGameLocator.PathComparer.Equals(Path.GetDirectoryName(path), Path.Combine(ItemRoot(appId, itemId), "revisions")))
            return false;
        return true;
    }

    internal void EnsureManagedRevisionHasNoLinks(string path) => EnsureNoLinks(_root, path);

    public Task<IDisposable> AcquireAsync(uint appId, ulong itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = ItemRoot(appId, itemId);
        EnsureNoLinks(_root, directory);
        Directory.CreateDirectory(directory);
        try { return Task.FromResult<IDisposable>(new FileStream(Path.Combine(directory, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException error) { throw new IOException("另一个应用实例正在处理这个 Mod，请等待该任务结束后重试。", error); }
    }

    public string GetStagingDirectory(uint appId, ulong itemId, string revision) =>
        Path.Combine(ItemRoot(appId, itemId), "staging", RevisionKey(revision));

    public async Task<WorkshopInstallResult?> FindVerifiedAsync(WorkshopContentPlan plan, string userDataDirectory, CancellationToken cancellationToken)
    {
        var directory = RevisionDirectory(plan);
        if (!Directory.Exists(directory)) return null;
        try { await VerifyAsync(plan, directory, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { return null; }
        // 上次若在内容发布后、wrapper 提交前退出，直接修复安装记录，不重新传输。
        return await PublishDescriptorAsync(plan, directory, userDataDirectory, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkshopInstallResult> InstallAsync(WorkshopContentPlan plan, string stagingDirectory, string userDataDirectory, CancellationToken cancellationToken)
    {
        var expectedStaging = GetStagingDirectory(plan.AppId, plan.PublishedFileId, plan.Revision);
        if (!SteamGameLocator.PathComparer.Equals(Path.GetFullPath(stagingDirectory), expectedStaging))
            throw new InvalidDataException("安装临时目录与下载任务不一致。");
        await VerifyAsync(plan, stagingDirectory, cancellationToken).ConfigureAwait(false);
        var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(Path.Combine(stagingDirectory, "descriptor.mod")));
        if (string.IsNullOrWhiteSpace(properties.Value("name"))) throw new InvalidDataException("Mod 描述文件缺少有效名称。");
        var destination = RevisionDirectory(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNoLinks(_root, destination);
        if (Directory.Exists(destination))
        {
            try
            {
                await VerifyAsync(plan, destination, cancellationToken).ConfigureAwait(false);
                Directory.Delete(stagingDirectory, true);
            }
            catch (InvalidDataException)
            {
                // 损坏的同修订目录留存供诊断，成功修订不会就地覆盖。
                Directory.Move(destination, destination + ".damaged-" + Guid.NewGuid().ToString("N"));
                Directory.Move(stagingDirectory, destination);
            }
        }
        else Directory.Move(stagingDirectory, destination);
        return await PublishDescriptorAsync(plan, destination, userDataDirectory, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkshopInstallResult> PublishDescriptorAsync(WorkshopContentPlan plan, string destination,
        string userDataDirectory, bool reused, CancellationToken cancellationToken)
    {
        var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(Path.Combine(destination, "descriptor.mod")));
        if (string.IsNullOrWhiteSpace(properties.Value("name"))) throw new InvalidDataException("Mod 描述文件缺少有效名称。");
        // 按完整属性重建外层描述，兼容同一行包含多个属性和跨行 block。
        var builder = new StringBuilder();
        foreach (var property in properties.Where(property => !new[] { "path", "archive", "remote_file_id" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(property.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new InvalidDataException("Mod 描述文件包含无效属性名。");
            builder.Append(property.Name).Append('=');
            if (property.Scalar is { } scalar) builder.Append('"').Append(Escape(scalar)).Append('"');
            else
            {
                builder.Append("{ ");
                foreach (var value in property.Block ?? [])
                    builder.Append(value is "{" or "}" or "=" ? value : "\"" + Escape(value) + "\"").Append(' ');
                builder.Append('}');
            }
            builder.Append('\n');
        }
        builder.Append("path=\"").Append(Escape(destination.Replace('\\', '/'))).Append("\"\nremote_file_id=\"")
            .Append(plan.PublishedFileId).Append("\"\n");
        var descriptor = builder.ToString();
        TextDataParser.Read(descriptor); // 提交前按扫描器使用的相同语法验收。
        var wrapper = Wrapper(plan, userDataDirectory);
        EnsureNoLinks(Path.GetFullPath(userDataDirectory), wrapper);
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        // 修订文件先发布；单个 wrapper 原子替换是安装提交点，崩溃前旧 wrapper 仍然可用。
        await AtomicWriteAsync(Path.Combine(ItemRoot(plan.AppId, plan.PublishedFileId), "installation.json"),
            JsonSerializer.Serialize(new { plan.AppId, plan.PublishedFileId, plan.Revision, ContentPath = destination,
                DescriptorPath = wrapper, InstalledAt = DateTimeOffset.UtcNow, Files = plan.Files }, Json), cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(wrapper, descriptor, cancellationToken).ConfigureAwait(false);
        return new(destination, wrapper, reused);
    }

    public static async Task VerifyAsync(WorkshopContentPlan plan, string directory, CancellationToken token)
    {
        if (plan.Files.Count == 0 || plan.Files.Count > 200000) throw new InvalidDataException("Mod 文件清单为空或过大。");
        var paths = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = SafeFilePath(directory, file.RelativePath);
            if (!paths.Add(path) || file.Size < 0 || file.Sha1.Length != 40)
                throw new InvalidDataException("Mod 清单包含冲突路径或无效的文件校验信息。");
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size) throw new InvalidDataException($"文件缺失或大小不符：{file.RelativePath}");
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!hash.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"文件校验失败：{file.RelativePath}");
        }
        if (!paths.Contains(SafeFilePath(directory, "descriptor.mod"))) throw new InvalidDataException("此内容缺少 descriptor.mod，无法作为 Paradox Mod 安装。");
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var folder))
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                EnsureNoLinks(directory, path);
                if (Directory.Exists(path)) pending.Push(path);
                else if (!paths.Contains(Path.GetFullPath(path))) throw new InvalidDataException("内容目录存在清单外文件，停止安装。");
            }
    }

    public static string SafeFilePath(string rootDirectory, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        // 文件系统根目录本身已带分隔符，不能再追加成 // 或 C:\\。
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Mod 文件路径越过内容目录。");
        EnsureNoLinks(root, path);
        return path;
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            normalized.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException("Mod 文件路径无效或试图越过内容目录。");
        return normalized;
    }

    private static void EnsureNoLinks(string root, string path)
    {
        // 检查受管根及其内部的每一级；允许操作系统自身的 /var 等祖先别名。
        var boundary = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        for (var current = Path.GetFullPath(path); current is not null && current != boundary; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装位置包含符号链接，无法安全写入。");
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async Task SaveTaskAsync(WorkshopDownloadSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = TaskPath(snapshot.AppId, snapshot.TaskId);
        var ordered = TaskPath(snapshot.AppId, snapshot.TaskId, OrderedTaskSuffix);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = ordered + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot with { AuthenticationUrl = null }, Json),
                cancellationToken).ConfigureAwait(false);
            // 为新文件原子发布排序上界。旧记录继续使用原路径。
            var canOrder = !File.Exists(path) && TrySetTaskOrder(temporary, snapshot.UpdatedAt.UtcDateTime);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, canOrder ? ordered : path, overwrite: true);
            if (!canOrder && File.Exists(ordered)) File.Delete(ordered);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string TaskPath(uint appId, Guid taskId, string suffix = ".json") =>
        Path.Combine(_root, "downloads", appId.ToString(), taskId.ToString("N") + suffix);

    private static bool TrySetTaskOrder(string path, DateTime updated)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, updated);
            // 有些文件系统会截断精度。只有可验证的时间上界才能提前停止读取。
            if (File.GetLastWriteTimeUtc(path) < updated && updated <= DateTime.MaxValue.AddSeconds(-2))
                File.SetLastWriteTimeUtc(path, updated.AddSeconds(2));
            return File.GetLastWriteTimeUtc(path) >= updated;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        { return false; }
    }

    public async Task DeleteTaskAsync(Guid taskId, uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (taskId == Guid.Empty || appId == 0) throw new ArgumentException("任务身份无效。");
        var path = TaskPath(appId, taskId);
        var ordered = TaskPath(appId, taskId, OrderedTaskSuffix);
        EnsureNoLinks(_root, path);
        EnsureNoLinks(_root, ordered);
        WorkshopDownloadSnapshot? saved;
        try { saved = await ReadSnapshotAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException)
        {
            try { saved = await ReadSnapshotAsync(ordered, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        }
        catch (DirectoryNotFoundException) { return; }
        IDisposable? processLock = null;
        try
        {
            // 历史读取可能把另一实例的活动任务展示为 Interrupted；不能删后又被其写回。
            if (saved is not null)
                processLock = await AcquireAsync(appId, saved.PublishedFileId, cancellationToken).ConfigureAwait(false);
            File.Delete(path);
            File.Delete(ordered);
            // 移除任务即清理本应用未完成的下载；已安装修订在 revisions 下，Steam 内容不由本工具管理。
            if (saved is { Owner: not WorkshopDownloadOwner.SteamClient, Revision: { Length: > 0 } })
            {
                try
                {
                    var staging = GetStagingDirectory(saved.AppId, saved.PublishedFileId, saved.Revision);
                    EnsureNoLinks(_root, staging);
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
                // 半成品清理失败不应让已删除的记录重新出现。
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        finally { processLock?.Dispose(); }
    }

    // 枚举、排序和小文件读取集中在线程池一次完成，界面线程无需等待磁盘元数据。
    public Task<IReadOnlyList<WorkshopDownloadSnapshot>> ReadTasksAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadTasks(cancellationToken), cancellationToken);

    private IReadOnlyList<WorkshopDownloadSnapshot> ReadTasks(CancellationToken cancellationToken)
    {
        var root = Path.Combine(_root, "downloads");
        if (!Directory.Exists(root)) return [];
        var recent = new PriorityQueue<WorkshopDownloadSnapshot, (DateTimeOffset UpdatedAt, long TieBreaker)>();
        var ordered = new List<FileInfo>();
        var legacy = new List<FileInfo>();
        long sequence = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Name.EndsWith(OrderedTaskSuffix, StringComparison.Ordinal)) ordered.Add(file);
            else legacy.Add(file);
        }
        if (legacy.Count > 0)
        {
            var stamp = GetLegacyHistoryStamp(legacy, cancellationToken);
            var cached = Volatile.Read(ref _legacyHistory);
            if (stamp is not null && cached?.Stamp == stamp)
            {
                foreach (var snapshot in cached.Recent) Add(snapshot);
            }
            else
            {
                var cacheable = stamp is not null;
                foreach (var file in legacy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { Add(TryReadHistory(file.FullName, skipUnavailable: false)); }
                    catch (Exception error) when (IsUnavailableHistoryFile(error)) { cacheable = false; }
                }
                if (cacheable) Volatile.Write(ref _legacyHistory, new(stamp!, GetRecent()));
            }
        }
        else Volatile.Write(ref _legacyHistory, null);
        foreach (var file in ordered.OrderByDescending(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recent.Count == WorkshopDownloadHistory.RecentLimit && recent.TryPeek(out _, out var oldest) &&
                file.LastWriteTimeUtc < oldest.UpdatedAt.UtcDateTime) break;
            // 降级写回旧路径后，若进程未能删除旧排序文件，则以已提交的原路径为准。
            if (legacy.Count > 0 && File.Exists(file.FullName[..^OrderedTaskSuffix.Length] + ".json")) continue;
            Add(TryReadHistory(file.FullName));
        }
        return GetRecent();

        WorkshopDownloadSnapshot[] GetRecent() =>
            recent.UnorderedItems.OrderByDescending(entry => entry.Priority).Select(entry => entry.Element).ToArray();

        void Add(WorkshopDownloadSnapshot? snapshot)
        {
            if (snapshot is null) return;
            var priority = (snapshot.UpdatedAt, -sequence++);
            if (recent.Count < WorkshopDownloadHistory.RecentLimit) recent.Enqueue(snapshot, priority);
            else recent.EnqueueDequeue(snapshot, priority);
        }
    }

    private static string? GetLegacyHistoryStamp(List<FileInfo> files, CancellationToken cancellationToken)
    {
        // 保持存量路径与时间戳不变，只在文件集合、原子替换或内容修改后重新解码。
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<long> fields = stackalloc long[4];
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fields[0] = file.FullName.Length;
                fields[1] = file.Length;
                fields[2] = file.LastWriteTimeUtc.Ticks;
                fields[3] = file.CreationTimeUtc.Ticks;
                hash.AppendData(MemoryMarshal.AsBytes(fields));
                hash.AppendData(MemoryMarshal.AsBytes(file.FullName.AsSpan()));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsUnavailableHistoryFile(Exception error) => error is IOException or UnauthorizedAccessException;

    private static WorkshopDownloadSnapshot? TryReadHistory(string path, bool skipUnavailable = true)
    {
        try
        {
            using var stream = OpenSnapshot(path);
            var snapshot = JsonSerializer.Deserialize<WorkshopDownloadSnapshot>(stream, Json);
            return snapshot is null || snapshot.IsTerminal ? snapshot : snapshot with
            {
                State = WorkshopDownloadState.Interrupted,
                Message = snapshot.Owner == WorkshopDownloadOwner.SteamClient
                    ? "上次跟踪已结束，Steam 可能仍在下载；点击重试重新查询。"
                    : "上次下载已中断，可在工坊重新下载并复用已校验文件",
                AuthenticationUrl = null
            };
        }
        catch (JsonException) { return null; }
        catch (Exception error) when (skipUnavailable && IsUnavailableHistoryFile(error)) { return null; }
    }

    public async Task<WorkshopDownloadSnapshot?> FindSteamItemTaskAsync(uint appId, ulong itemId, Guid? knownTaskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (knownTaskId is { } id)
        {
            var known = await ReadSteamHistoryTaskAsync(appId, id, cancellationToken).ConfigureAwait(false);
            if (MatchesSteamItem(known, appId, itemId) && known!.TaskId == id) return known;
        }
        WorkshopDownloadSnapshot? latest = null;
        foreach (var path in GetTaskFiles(appId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = await ReadSteamHistoryFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (MatchesSteamItem(saved, appId, itemId) && (latest is null || saved!.UpdatedAt > latest.UpdatedAt))
                latest = saved;
        }
        return latest;
    }

    public async Task<bool> DeleteSteamItemTasksAsync(uint appId, ulong itemId, CancellationToken cancellationToken,
        Guid? taskId = null, DateTimeOffset? expectedUpdatedAt = null)
    {
        // 整个物品历史清理保持同一把跨实例锁，包含之前因占用而没能合并的旧记录。
        using var itemLock = await AcquireAsync(appId, itemId, cancellationToken).ConfigureAwait(false);
        if (expectedUpdatedAt is { } expected)
        {
            if (taskId is not { } expectedId) return false;
            var current = await ReadSteamHistoryTaskAsync(appId, expectedId, cancellationToken).ConfigureAwait(false);
            if (!MatchesSteamItem(current, appId, itemId) || current!.TaskId != expectedId || current.UpdatedAt != expected)
                return false;
        }
        var targets = new List<(string Path, bool Selected)>();
        var selectedPath = taskId is { } id ? TaskPath(appId, id) : null;
        var selectedOrderedPath = taskId is { } orderedId ? TaskPath(appId, orderedId, OrderedTaskSuffix) : null;
        var selectedExists = false;
        foreach (var path in GetTaskFiles(appId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = await ReadSteamHistoryFileAsync(path, cancellationToken).ConfigureAwait(false);
            var selected = SteamGameLocator.PathComparer.Equals(path, selectedPath) ||
                SteamGameLocator.PathComparer.Equals(path, selectedOrderedPath);
            selectedExists |= selected;
            if (MatchesSteamItem(saved, appId, itemId) || selected && saved is null)
                targets.Add((path, selected));
            else if (selected)
                throw new InvalidDataException("下载记录与目标 Mod 不一致，无法移除。");
        }
        // 其他实例可能已经移除所选记录并创建了新的任务。
        if (taskId is not null && !selectedExists) return true;
        // 先移除旧副本，失败时尽量保留当前卡片对应的磁盘记录。
        foreach (var target in targets.OrderBy(target => target.Selected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(target.Path); }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }

    public async Task DeleteSteamItemTaskIfUnchangedAsync(WorkshopDownloadSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var itemLock = await AcquireAsync(snapshot.AppId, snapshot.PublishedFileId, cancellationToken).ConfigureAwait(false);
        var path = TaskPath(snapshot.AppId, snapshot.TaskId);
        var ordered = TaskPath(snapshot.AppId, snapshot.TaskId, OrderedTaskSuffix);
        var saved = await ReadSteamHistoryTaskAsync(snapshot.AppId, snapshot.TaskId, cancellationToken).ConfigureAwait(false);
        // 历史读取之后，另一实例可能已经用这条记录完成了一次新的下载。
        if (!MatchesSteamItem(saved, snapshot.AppId, snapshot.PublishedFileId) ||
            saved!.TaskId != snapshot.TaskId || saved.UpdatedAt != snapshot.UpdatedAt) return;
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        File.Delete(ordered);
    }

    private async Task<WorkshopDownloadSnapshot?> ReadSteamHistoryTaskAsync(uint appId, Guid taskId, CancellationToken token) =>
        await ReadSteamHistoryFileAsync(TaskPath(appId, taskId), token).ConfigureAwait(false)
        ?? await ReadSteamHistoryFileAsync(TaskPath(appId, taskId, OrderedTaskSuffix), token).ConfigureAwait(false);

    private string[] GetTaskFiles(uint appId)
    {
        var directory = Path.Combine(_root, "downloads", appId.ToString());
        EnsureNoLinks(_root, directory);
        try { return Directory.GetFiles(directory, "*.json"); }
        catch (DirectoryNotFoundException) { return []; }
    }

    private async Task<WorkshopDownloadSnapshot?> ReadSteamHistoryFileAsync(string path, CancellationToken token)
    {
        try
        {
            EnsureNoLinks(_root, path);
            return await ReadSnapshotAsync(path, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool MatchesSteamItem(WorkshopDownloadSnapshot? snapshot, uint appId, ulong itemId) =>
        snapshot is { Owner: WorkshopDownloadOwner.SteamClient, TaskId: var id } && id != Guid.Empty &&
        snapshot.AppId == appId && snapshot.PublishedFileId == itemId;

    private static async Task<WorkshopDownloadSnapshot?> ReadSnapshotAsync(string path, CancellationToken token)
    {
        await using var stream = OpenSnapshot(path);
        return await JsonSerializer.DeserializeAsync<WorkshopDownloadSnapshot>(stream, Json, token).ConfigureAwait(false);
    }

    // 允许另一项的并发清理在读句柄存在时删除文件（Windows 需要 FileShare.Delete）。
    private static FileStream OpenSnapshot(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
