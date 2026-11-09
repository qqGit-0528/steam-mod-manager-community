using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class WorkshopDownloadStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ck3-download-test-").FullName;
    private static readonly byte[] Descriptor = Encoding.UTF8.GetBytes("name=\"下载测试\"\nsupported_version=\"1.20.*\"\nversion=\"1.0\"\n");
    private static WorkshopContentPlan Plan(string revision = "1") => new(Ck3.AppId, 101, revision,
        [new("descriptor.mod", Descriptor.Length, Convert.ToHexString(SHA1.HashData(Descriptor)))]);
    private async Task<string> Stage(FileWorkshopDownloadStore store, WorkshopContentPlan plan)
    {
        var directory = store.GetStagingDirectory(plan.AppId, plan.PublishedFileId, plan.Revision);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "descriptor.mod"), Descriptor, TestContext.Current.CancellationToken);
        return directory;
    }

    [Fact]
    public async Task RecentHistoryMergesOldFilesAndOrderedFilesByTheirRecordedTime()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new FileWorkshopDownloadStore(_directory);
        var appDirectory = Directory.CreateDirectory(Path.Combine(_directory, "downloads", Ck3.AppId.ToString())).FullName;
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = Enumerable.Range(0, 105).Select(index => new WorkshopDownloadSnapshot(
            Guid.NewGuid(), Ck3.AppId, (ulong)index + 1, "测试", WorkshopDownloadState.Completed, 0, null,
            time.AddSeconds(index))).ToArray();
        foreach (var entry in entries) await store.SaveTaskAsync(entry, token);

        var legacy = entries[0] with { TaskId = Guid.NewGuid(), PublishedFileId = 200, UpdatedAt = time.AddHours(1),
            State = WorkshopDownloadState.Downloading };
        var legacyPath = Path.Combine(appDirectory, legacy.TaskId.ToString("N") + ".json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacy), token);
        File.SetLastWriteTimeUtc(legacyPath, time.AddDays(-1).UtcDateTime);
        var broken = Path.Combine(appDirectory, Guid.NewGuid().ToString("N") + ".ordered.json");
        await File.WriteAllTextAsync(broken, "{", token);
        File.SetLastWriteTimeUtc(broken, time.AddDays(1).UtcDateTime);

        var reopened = new FileWorkshopDownloadStore(_directory);
        var history = await reopened.ReadTasksAsync(token);
        Assert.Equal(WorkshopDownloadHistory.RecentLimit, history.Count);
        Assert.Equal(legacy.TaskId, history[0].TaskId);
        Assert.Equal(WorkshopDownloadState.Interrupted, history[0].State);
        Assert.Equal(entries[^1].TaskId, history[1].TaskId);
        Assert.Equal(entries[6].TaskId, history[^1].TaskId);
        var changed = legacy with { Title = "由另一实例更新", UpdatedAt = time.AddHours(2) };
        await store.SaveTaskAsync(changed, token);
        Assert.Equal(changed.Title, (await reopened.ReadTasksAsync(token))[0].Title);
        await reopened.DeleteTaskAsync(entries[^1].TaskId, Ck3.AppId, token);
        Assert.Equal(entries[^2].TaskId, (await reopened.ReadTasksAsync(token))[1].TaskId);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public async Task VerifiedContentIsAtomicallyPublishedAndRecognizedByRealScanner()
    {
        var store = new FileWorkshopDownloadStore(_directory); var plan = Plan();
        var user = Path.Combine(_directory, "user"); var token = TestContext.Current.CancellationToken;
        using var handle = await store.AcquireAsync(plan.AppId, plan.PublishedFileId, token);
        var staged = await Stage(store, plan);
        Assert.Empty((await new Ck3ModScanner().ScanAsync(user, [], token)).Mods);
        var result = await store.InstallAsync(plan, staged, user, token);
        Assert.False(Directory.Exists(staged));
        var mod = Assert.Single((await new Ck3ModScanner().ScanAsync(user, [], token)).Mods);
        Assert.True(mod.IsAvailable);
        Assert.Equal(101UL, mod.WorkshopId);
        Assert.Equal(result.ContentPath, mod.ContentPath);
        Assert.Equal("下载测试", mod.Name);
        Assert.True((await store.FindVerifiedAsync(plan, user, token))!.Reused);
    }

    [Fact]
    public async Task CorruptUpdateNeverChangesPreviousWrapperOrDeletesItsContent()
    {
        var store = new FileWorkshopDownloadStore(_directory); var token = TestContext.Current.CancellationToken;
        var user = Path.Combine(_directory, "user");
        var first = Plan(); var installed = await store.InstallAsync(first, await Stage(store, first), user, token);
        var before = await File.ReadAllTextAsync(installed.DescriptorPath, token);
        var update = Plan("2"); var staging = await Stage(store, update);
        await File.WriteAllTextAsync(Path.Combine(staging, "descriptor.mod"), "damaged", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(update, staging, user, token));
        Assert.Equal(before, await File.ReadAllTextAsync(installed.DescriptorPath, token));
        Assert.True(File.Exists(Path.Combine(installed.ContentPath, "descriptor.mod")));
    }

    [Theory]
    [InlineData("descriptor.mod")]
    [InlineData("localization/simp_chinese/names.yml")]
    public void ContentPathsAcceptAFileSystemRootAsTheValidationBoundary(string relativePath)
    {
        // Finder / LaunchServices 启动的 .app 工作目录通常就是文件系统根目录。
        var root = Path.GetPathRoot(Path.GetFullPath(_directory))!;
        Assert.Equal(Path.GetFullPath(Path.Combine(root, relativePath)), FileWorkshopDownloadStore.SafeFilePath(root, relativePath));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\escape")]
    [InlineData("a/../../escape")]
    [InlineData("a//bad")]
    public void ContentPathsCannotEscapeStaging(string path) =>
        Assert.Throws<InvalidDataException>(() => FileWorkshopDownloadStore.SafeFilePath(_directory, path));

    [Fact]
    public void TrailingSeparatorDoesNotAllowASymbolicLinkAsTheContentRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateDirectory(Path.Combine(_directory, "outside")).FullName;
        var link = Path.Combine(_directory, "linked-root");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<InvalidDataException>(() => FileWorkshopDownloadStore.SafeFilePath(link + Path.DirectorySeparatorChar, "descriptor.mod"));
    }

    [Fact]
    public async Task CrossInstanceLockExcludesSecondWriterAndReleasesAfterExit()
    {
        var first = new FileWorkshopDownloadStore(_directory); var second = new FileWorkshopDownloadStore(_directory);
        var token = TestContext.Current.CancellationToken;
        using (await first.AcquireAsync(Ck3.AppId, 101, token))
            await Assert.ThrowsAsync<IOException>(() => second.AcquireAsync(Ck3.AppId, 101, token));
        using var released = await second.AcquireAsync(Ck3.AppId, 101, token);
        using var otherGame = await first.AcquireAsync(GameCatalog.Hoi4.AppId, 101, token);
    }

    [Fact]
    public async Task HistoryRemovesAuthenticationSecretsAndMarksUnfinishedTaskInterrupted()
    {
        var store = new FileWorkshopDownloadStore(_directory); var token = TestContext.Current.CancellationToken;
        var snapshot = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "测试", WorkshopDownloadState.AwaitingAuthentication,
            0, null, DateTimeOffset.UtcNow, AuthenticationUrl: "https://s.team/q/secret-challenge");
        await store.SaveTaskAsync(snapshot, token);
        var history = Assert.Single(await new FileWorkshopDownloadStore(_directory).ReadTasksAsync(token));
        Assert.Equal(WorkshopDownloadState.Interrupted, history.State);
        Assert.Null(history.AuthenticationUrl);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("secret-challenge", await File.ReadAllTextAsync(file, token));
    }

    [Fact]
    public async Task DescriptorWithSameLinePropertiesAndMultilineTagsRemainsReadableAfterInstall()
    {
        var content = Encoding.UTF8.GetBytes("path=\"mod/dev\" tags={\n \"Gameplay\"\n}\nname=\"合法 Mod\"\nsupported_version=\"1.20.*\"\nremote_file_id=\"999\"\n");
        var plan = new WorkshopContentPlan(Ck3.AppId, 202, "multiline", [new("descriptor.mod", content.Length, Convert.ToHexString(SHA1.HashData(content)))]);
        var store = new FileWorkshopDownloadStore(_directory); var token = TestContext.Current.CancellationToken;
        var staging = store.GetStagingDirectory(plan.AppId, plan.PublishedFileId, plan.Revision);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "descriptor.mod"), content, token);
        var user = Path.Combine(_directory, "user");
        await store.InstallAsync(plan, staging, user, token);
        var mod = Assert.Single((await new Ck3ModScanner().ScanAsync(user, [], token)).Mods);
        Assert.True(mod.IsAvailable);
        Assert.Equal("合法 Mod", mod.Name);
        Assert.Equal(202UL, mod.WorkshopId);
    }

    [Fact]
    public async Task PublishedContentWithoutWrapperIsRecoveredWithoutAnotherDownload()
    {
        var store = new FileWorkshopDownloadStore(_directory); var plan = Plan();
        var token = TestContext.Current.CancellationToken; var user = Path.Combine(_directory, "user");
        var first = await store.InstallAsync(plan, await Stage(store, plan), user, token);
        File.Delete(first.DescriptorPath);
        var recovered = await store.FindVerifiedAsync(plan, user, token);
        Assert.NotNull(recovered);
        Assert.True(recovered.Reused);
        Assert.True(File.Exists(recovered.DescriptorPath));
        Assert.Single((await new Ck3ModScanner().ScanAsync(user, [], token)).Mods);
    }

    [Fact]
    public async Task DeletingTaskHistoryPreservesInstalledFilesAndOtherGamesRecords()
    {
        var store = new FileWorkshopDownloadStore(_directory); var plan = Plan(); var token = TestContext.Current.CancellationToken;
        var result = await store.InstallAsync(plan, await Stage(store, plan), Path.Combine(_directory, "user"), token);
        var task = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "测试", WorkshopDownloadState.Completed, 0, null,
            DateTimeOffset.UtcNow, InstalledPath: result.ContentPath);
        var other = task with { TaskId = Guid.NewGuid(), AppId = GameCatalog.Hoi4.AppId };
        await store.SaveTaskAsync(task, token); await store.SaveTaskAsync(other, token);
        await store.DeleteTaskAsync(task.TaskId, task.AppId, token);
        Assert.Equal(other.TaskId, Assert.Single(await store.ReadTasksAsync(token)).TaskId);
        Assert.True(File.Exists(result.DescriptorPath));
        Assert.True(File.Exists(Path.Combine(result.ContentPath, "descriptor.mod")));
    }

    [Fact]
    public async Task DeletingCancelledTaskRemovesStagingButKeepsInstalledContent()
    {
        var store = new FileWorkshopDownloadStore(_directory); var token = TestContext.Current.CancellationToken;
        var user = Path.Combine(_directory, "user");
        var installed = await store.InstallAsync(Plan(), await Stage(store, Plan()), user, token);
        var update = Plan("2"); var staging = await Stage(store, update);
        var task = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "取消的任务", WorkshopDownloadState.Cancelled,
            0, null, DateTimeOffset.UtcNow, Revision: update.Revision);
        await store.SaveTaskAsync(task, token);
        await store.DeleteTaskAsync(task.TaskId, task.AppId, token);
        Assert.False(Directory.Exists(staging));
        Assert.Empty(await store.ReadTasksAsync(token));
        Assert.True(File.Exists(Path.Combine(installed.ContentPath, "descriptor.mod")));
    }

    [Fact]
    public async Task AnotherInstanceCannotRemoveHistoryWhileItsDownloadIsStillActive()
    {
        var writer = new FileWorkshopDownloadStore(_directory); var reader = new FileWorkshopDownloadStore(_directory);
        var token = TestContext.Current.CancellationToken;
        var snapshot = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "下载中", WorkshopDownloadState.Queued, 0, null, DateTimeOffset.UtcNow);
        using (await writer.AcquireAsync(Ck3.AppId, 101, token))
        {
            await writer.SaveTaskAsync(snapshot, token);
            Assert.Equal(WorkshopDownloadState.Interrupted, Assert.Single(await reader.ReadTasksAsync(token)).State);
            await Assert.ThrowsAsync<IOException>(() => reader.DeleteTaskAsync(snapshot.TaskId, snapshot.AppId, token));
            Assert.Single(await reader.ReadTasksAsync(token));
        }
        await reader.DeleteTaskAsync(snapshot.TaskId, snapshot.AppId, token);
        Assert.Empty(await writer.ReadTasksAsync(token));
    }

    [Fact]
    public async Task UnexpectedFilesAndSymlinkDirectoriesAreRejected()
    {
        var store = new FileWorkshopDownloadStore(_directory); var plan = Plan(); var token = TestContext.Current.CancellationToken;
        var staging = await Stage(store, plan);
        await File.WriteAllTextAsync(Path.Combine(staging, "extra.txt"), "not in manifest", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileWorkshopDownloadStore.VerifyAsync(plan, staging, token));
        File.Delete(Path.Combine(staging, "extra.txt"));
        if (!OperatingSystem.IsWindows())
        {
            var outside = Directory.CreateDirectory(Path.Combine(_directory, "outside")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(staging, "link"), outside);
            await Assert.ThrowsAsync<InvalidDataException>(() => FileWorkshopDownloadStore.VerifyAsync(plan, staging, token));
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
