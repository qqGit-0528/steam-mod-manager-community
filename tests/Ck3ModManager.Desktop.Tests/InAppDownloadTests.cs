using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop.Tests;

public sealed class InAppDownloadTests
{
    [AvaloniaFact]
    public async Task RefusedHistoryDeletionKeepsTheCardUntilDiskRemovalSucceeds()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var environment = await fixture.DiscoverAsync(new(), token);
        var directory = Path.Combine(Path.GetDirectoryName(environment.UserDataDirectory)!, "data", "downloads", Ck3.AppId.ToString());
        var store = fixture.Store;
        var saved = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "已完成 Mod",
            WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow) { Owner = WorkshopDownloadOwner.SteamClient };
        await store.SaveTaskAsync(saved, token);
        using var service = new WorkshopDownloadService(new NativeChecks(environment.UserDataDirectory), store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(token);
        var row = Assert.Single(vm.Downloads);
        if (OperatingSystem.IsWindows())
        {
            using var blocker = new FileStream(Assert.Single(Directory.GetFiles(directory, "*.json")),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await CheckRefusal();
        }
        else
        {
            var permissions = File.GetUnixFileMode(directory);
            try
            {
                File.SetUnixFileMode(directory, permissions & ~UnixFileMode.UserWrite);
                await CheckRefusal();
            }
            finally { File.SetUnixFileMode(directory, permissions); }
        }
        await row.RemoveAsync();
        Assert.Empty(vm.Downloads);
        Assert.Empty(await fixture.Store.ReadTasksAsync(token));
        Assert.Null(vm.ErrorMessage);

        async Task CheckRefusal()
        {
            await row.RemoveAsync();
            Assert.Same(row, Assert.Single(vm.Downloads));
            Assert.Contains("无法移除任务记录", vm.ErrorMessage);
            Assert.Equal(saved.TaskId, Assert.Single(await fixture.Store.ReadTasksAsync(token)).TaskId);
        }
    }

    [AvaloniaFact]
    public async Task DownloadCardsKeepRecentTerminalRowsAndStillShowActiveTasks()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var environment = await fixture.DiscoverAsync(new(), token);
        var now = DateTimeOffset.UtcNow;
        for (ulong i = 1; i <= WorkshopDownloadHistory.RecentLimit; i++)
        {
            var snapshot = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, i, "历史任务",
                WorkshopDownloadState.Completed, 0, null, now.AddMinutes(-(double)i));
            await fixture.Store.SaveTaskAsync(snapshot, token);
        }
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(token);
        Assert.Equal(WorkshopDownloadHistory.RecentLimit, vm.Downloads.Count);
        var task = service.Start(await fixture.GetItemAsync(101, token), environment.UserDataDirectory);
        await fixture.Provider.Started.Task.WaitAsync(token);
        await PumpUntil(() => vm.Downloads.Count == WorkshopDownloadHistory.RecentLimit + 1);
        fixture.Provider.Release.TrySetResult();
        await task.Completion.WaitAsync(token);
        await PumpUntil(() => vm.Downloads.Count == WorkshopDownloadHistory.RecentLimit);
        Assert.Contains(vm.Downloads, row => row.Snapshot.TaskId == task.Snapshot.TaskId && row.Snapshot.IsTerminal);
        Assert.DoesNotContain(vm.Downloads, row => row.Snapshot.PublishedFileId == WorkshopDownloadHistory.RecentLimit);
    }

    [AvaloniaFact]
    public async Task CheckingUnchangedSteamContentKeepsOneRowAndHistoryAndIgnoresRepeatedClicks()
    {
        using var fixture = new Fixture();
        var content = Directory.CreateTempSubdirectory("steam-check-").FullName;
        try
        {
            var native = new NativeChecks(content);
            using var service = new WorkshopDownloadService(native, fixture.Store);
            using var vm = fixture.Create(service);
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            vm.WorkshopInput = "101"; await vm.LookupWorkshopAsync();
            vm.DownloadWorkshopCommand.Execute(null);
            await PumpUntil(() => service.Tasks.Count == 1 && service.Tasks[0].Completion.IsCompleted && vm.Downloads.Count == 1);
            var row = vm.Downloads[0]; var id = row.Snapshot.TaskId;
            for (var i = 0; i < 30; i++) row.RetryCommand.Execute(null);
            await PumpUntil(() => native.Calls == 2 && row.CanRetry);
            Assert.Same(row, Assert.Single(vm.Downloads));
            Assert.Equal(id, row.Snapshot.TaskId); Assert.True(row.Snapshot.IsUpToDate);
            Assert.Equal("已是最新，无需下载", vm.Status);
            Assert.Single(service.Tasks);
            Assert.Equal(id, Assert.Single(await fixture.Store.ReadTasksAsync(TestContext.Current.CancellationToken)).TaskId);
            // 升级前产生的重复终态记录会合并，重启后仍只有一项。
            await fixture.Store.SaveTaskAsync(row.Snapshot with { TaskId = Guid.NewGuid(), UpdatedAt = row.Snapshot.UpdatedAt.AddSeconds(-1) }, TestContext.Current.CancellationToken);
            using var restartedService = new WorkshopDownloadService(native, fixture.Store);
            using var restarted = fixture.Create(restartedService);
            await restarted.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(id, Assert.Single(restarted.Downloads).Snapshot.TaskId);
            Assert.Single(await fixture.Store.ReadTasksAsync(TestContext.Current.CancellationToken));
            restarted.WorkshopInput = "101"; await restarted.LookupWorkshopAsync();
            restarted.DownloadWorkshopCommand.Execute(null);
            await PumpUntil(() => restartedService.Tasks.Count == 1 && restartedService.Tasks[0].Completion.IsCompleted);
            Assert.Equal(id, Assert.Single(restarted.Downloads).Snapshot.TaskId);
            Assert.Single(await fixture.Store.ReadTasksAsync(TestContext.Current.CancellationToken));
            await restarted.Downloads[0].RemoveAsync();
            using var finalService = new WorkshopDownloadService(native, fixture.Store);
            using var finalVm = fixture.Create(finalService);
            await finalVm.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Empty(finalVm.Downloads);
        }
        finally { Directory.Delete(content, true); }
    }

    [AvaloniaFact]
    public async Task ProfileImportPreviewsBeforeSavingAndReusesAnActiveDownload()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        var token = TestContext.Current.CancellationToken;
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        using var file = new MemoryStream();
        var original = new ModProfile(Guid.NewGuid(), "共享配置", "1.20.1",
        [
            new("b", "第二个 Mod", "/other/b", 102, null),
            new("off", "关闭的 Mod", "/other/off", 103, null) { Enabled = false },
            new("a", "第一个 Mod", "/other/a", 101, null)
        ], DateTimeOffset.UtcNow);
        await PortableProfileService.WriteAsync(file, GameCatalog.Ck3, original, token);

        file.Position = 0;
        await vm.PreviewProfileImportAsync(file);
        Assert.Equal("导入并下载 2 项", vm.ConfirmProfileImportText);
        Assert.Equal(new[] { "启用", "禁用", "启用" }, vm.ProfileImportRows.Select(row => row.EnabledText));
        Assert.Empty(vm.Profiles);
        Assert.Empty(service.Tasks);
        vm.CancelProfileImportCommand.Execute(null);
        Assert.False(vm.HasProfileImportPreview);
        Assert.Empty(service.Tasks);

        var environment = await fixture.DiscoverAsync(new(), token);
        var existing = service.Start(await fixture.GetItemAsync(101, token), environment.UserDataDirectory);
        await fixture.Provider.Started.Task.WaitAsync(token);
        file.Position = 0;
        await vm.PreviewProfileImportAsync(file);
        await vm.ConfirmProfileImportAsync();
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.False(vm.HasProfileImportPreview);
        Assert.Equal(new[] { (102UL, true), (103UL, false), (101UL, true) },
            vm.ProfileMods.Select(row => (row.Mod.WorkshopId!.Value, row.Enabled)));
        Assert.Equal(2, service.Tasks.Count);
        Assert.Same(existing, service.Tasks.Single(task => task.Snapshot.PublishedFileId == 101));
        Assert.DoesNotContain(service.Tasks, task => task.Snapshot.PublishedFileId == 103);

        fixture.Provider.Release.TrySetResult();
        await Task.WhenAll(service.Tasks.Select(task => task.Completion)).WaitAsync(token);
        await PumpUntil(() => vm.ProfileMods.Count(row => !PortableProfileService.IsUnresolved(row.Mod)) == 2);
        Assert.Equal(new[] { 102UL, 103UL, 101UL }, vm.ProfileMods.Select(row => row.Mod.WorkshopId!.Value));
    }

    [AvaloniaFact]
    public async Task ProfileImportRequiresNewApprovalWhenAnInstalledModGoesMissingAndSavesBeforeQueueing()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        var token = TestContext.Current.CancellationToken;
        var environment = await fixture.DiscoverAsync(new(), token);
        var modRoot = Directory.CreateDirectory(Path.Combine(environment.UserDataDirectory, "mod")).FullName;
        var content = Directory.CreateDirectory(Path.Combine(modRoot, "local")).FullName;
        var descriptor = Path.Combine(modRoot, "local.mod");
        await File.WriteAllTextAsync(descriptor, """name="本地副本" path="mod/local" remote_file_id="101" """, token);

        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        Assert.StartsWith("本地", Assert.Single(vm.InstalledMods).Detail);
        var profile = new ModProfile(Guid.NewGuid(), "共享配置", null,
            [new("source", "工坊 Mod", "/source", 101, null)], DateTimeOffset.UtcNow);
        using var file = new MemoryStream();
        await PortableProfileService.WriteAsync(file, GameCatalog.Ck3, profile, token);
        file.Position = 0;
        await vm.PreviewProfileImportAsync(file);
        Assert.Equal("确认导入", vm.ConfirmProfileImportText);

        File.Delete(descriptor);
        Directory.Delete(content);
        await vm.ConfirmProfileImportAsync();
        Assert.Equal("导入并下载 1 项", vm.ConfirmProfileImportText);
        Assert.Empty(vm.Profiles);
        Assert.Empty(service.Tasks);

        fixture.FailProfileSave = true;
        await vm.ConfirmProfileImportAsync();
        Assert.True(vm.HasError);
        Assert.Empty(vm.Profiles);
        Assert.Empty(service.Tasks);

        fixture.FailProfileSave = false;
        fixture.Provider.Release.TrySetResult();
        await vm.ConfirmProfileImportAsync();
        Assert.Single(vm.Profiles);
        var task = Assert.Single(service.Tasks);
        await task.Completion.WaitAsync(token);
        await PumpUntil(() => vm.Downloads.FirstOrDefault()?.HasInstalledPath == true);
    }

    [AvaloniaFact]
    public async Task ImportShowsExpectedAndLocalVersionsAndRechecksAnExplicitChoice()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        var token = TestContext.Current.CancellationToken;
        var environment = await fixture.DiscoverAsync(new(), token);
        var modRoot = Directory.CreateDirectory(Path.Combine(environment.UserDataDirectory, "mod")).FullName;
        Directory.CreateDirectory(Path.Combine(modRoot, "local"));
        var descriptor = Path.Combine(modRoot, "local.mod");
        await File.WriteAllTextAsync(descriptor, """name="本地副本" path="mod/local" remote_file_id="101" version="2.0" """, token);

        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        using var file = new MemoryStream();
        var profile = new ModProfile(Guid.NewGuid(), "共享配置", null,
            [new("source", "工坊 Mod", "/source", 101, null, "1.0")], DateTimeOffset.UtcNow);
        await PortableProfileService.WriteAsync(file, GameCatalog.Ck3, profile, token);
        file.Position = 0;
        await vm.PreviewProfileImportAsync(file);
        var row = Assert.Single(vm.ProfileImportRows);
        Assert.Equal("清单 1.0 · 本机 2.0", row.VersionComparison);
        Assert.Null(row.SelectedChoice!.Candidate);
        row.SelectedChoice = row.Choices[1];

        await File.WriteAllTextAsync(descriptor, """name="本地副本" path="mod/local" remote_file_id="101" version="3.0" """, token);
        await vm.ConfirmProfileImportAsync();
        Assert.Empty(vm.Profiles);
        Assert.True(vm.HasProfileImportPreview);
        row = Assert.Single(vm.ProfileImportRows);
        Assert.Equal("清单 1.0 · 本机 3.0", row.VersionComparison);
        Assert.Null(row.SelectedChoice!.Candidate);

        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 900 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == row.VersionComparison);
            SaveBatch2Preview(window, "profile-import-versions.png");
            var picker = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
                combo => combo.IsEffectivelyVisible && ReferenceEquals(combo.ItemsSource, row.Choices));
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(row.Choices[1], row.SelectedChoice);
            await vm.ConfirmProfileImportAsync();
            Assert.False(vm.HasProfileImportPreview);
            Assert.Equal("3.0", Assert.Single(vm.ProfileMods).Mod.ModVersion);
            Assert.Empty(service.Tasks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DependencyPreviewIsReadOnlyUntilConfirmedAndQueuesADiamondOnce()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service, new DependencyMap());
        var token = TestContext.Current.CancellationToken;
        var environment = await fixture.DiscoverAsync(new(), token);
        var modRoot = Directory.CreateDirectory(Path.Combine(environment.UserDataDirectory, "mod")).FullName;
        Directory.CreateDirectory(Path.Combine(modRoot, "root"));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "root.mod"),
            """name="配置根 Mod" path="mod/root" remote_file_id="101" """, token);
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        vm.CreateProfile();
        vm.SelectedInstalledMod = Assert.Single(vm.InstalledMods);
        vm.AddSelectedMod();

        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 900 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var check = window.FindControl<Button>("CheckDependenciesButton")!;
            Assert.True(check.IsEffectivelyEnabled);
            check.Command!.Execute(null);
            await PumpUntil(() => vm.HasDependencyReport && !vm.IsBusy);
            var confirm = window.FindControl<Button>("ConfirmDependencyDownloadButton")!;
            Assert.True(confirm.IsEffectivelyEnabled);
            Assert.Contains("3", vm.DependencyDownloadLabel);
            Assert.Equal(4, vm.DependencyFindings.Count);
            Assert.Empty(service.Tasks);
            SaveBatch2Preview(window, "profile-dependencies.png");

            vm.Section = "overview";
            Assert.False(vm.HasDependencyReport);
            Assert.Empty(service.Tasks);
            vm.Section = "profiles";
            await vm.CheckDependenciesAsync();
            await vm.DownloadDependenciesAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal(new ulong[] { 102, 103, 104 }, service.Tasks.Select(task => task.Snapshot.PublishedFileId).Order());
            fixture.Provider.Release.TrySetResult();
            await Task.WhenAll(service.Tasks.Select(task => task.Completion)).WaitAsync(token);
        }
        finally { window.Close(); }
    }

    private static void SaveBatch2Preview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("CK3_BATCH2_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(directory))
            window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name),
                Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class DependencyMap : IWorkshopDependencySource
    {
        public Task<IReadOnlyList<ulong>?> ReadRequiredItemsAsync(uint appId, ulong itemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ulong>?>(itemId switch
            {
                101 => [102, 103],
                102 or 103 => [104],
                _ => []
            });
    }

    private sealed class NativeChecks(string content) : ISteamClientWorkshopDownloader
    {
        public int Calls;
        public Task<SteamClientInstallation> DownloadAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        { var call = Interlocked.Increment(ref Calls); return Task.FromResult(new SteamClientInstallation(content, 100, call > 1, Revision: "steam-client:1")); }
    }

    [AvaloniaFact]
    public async Task ClickingDownloadShowsRealProgressAndQrThenInstallsWithoutExternalNavigation()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.WorkshopInput = "101";
        await vm.LookupWorkshopAsync();
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("DownloadWorkshopButton")!;
            Assert.Same(vm.DownloadWorkshopCommand, button.Command);
            button.Command!.Execute(null);
            await fixture.Provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await PumpUntil(() => vm.Downloads.FirstOrDefault()?.HasQrCode == true);
            Assert.True(vm.IsDownloads);
            Assert.Equal("等待下载授权", Assert.Single(vm.Downloads).StateText);
            Assert.Equal(0, fixture.OpenWorkshopCalls);
            vm.DownloadWorkshopCommand.Execute(null);
            Assert.Single(service.Tasks);
            fixture.Provider.Release.TrySetResult();
            await service.Tasks[0].Completion.WaitAsync(TestContext.Current.CancellationToken);
            await PumpUntil(() => vm.InstalledMods.Count == 1);
            var row = Assert.Single(vm.Downloads);
            Assert.Equal("下载完成", row.StateText);
            Assert.Equal("当前已安装", row.InstallationStatus);
            Assert.Equal(100, row.Percent);
            Assert.False(row.HasQrCode);
            Assert.True(row.HasInstalledPath);
            Assert.True(File.Exists(Path.Combine(row.InstalledPath, "descriptor.mod")));
            Assert.Equal(101UL, Assert.Single(vm.InstalledMods).Mod.WorkshopId);
            Assert.Equal(0, fixture.OpenWorkshopCalls);
            row.OpenDirectoryCommand.Execute(null);
            Assert.Equal(row.InstalledPath, fixture.OpenedDirectory);
            var screenshots = Environment.GetEnvironmentVariable("CK3_DOWNLOAD_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(screenshots))
            {
                Directory.CreateDirectory(screenshots);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "downloads-installed.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            using var restarted = fixture.Create(new WorkshopDownloadService(fixture.Provider, fixture.Store));
            await restarted.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkshopDownloadState.Completed, Assert.Single(restarted.Downloads).Snapshot.State);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledTaskCanBeRemovedAndDoesNotReappearAfterRestart()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.WorkshopInput = "101"; await vm.LookupWorkshopAsync();
        vm.DownloadWorkshopCommand.Execute(null);
        await fixture.Provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await PumpUntil(() => vm.Downloads.Count == 1);
        Assert.True(vm.Downloads[0].CanRemove);
        service.Tasks[0].Cancel();
        await service.Tasks[0].Completion.WaitAsync(TestContext.Current.CancellationToken);
        await PumpUntil(() => vm.Downloads[0].CanRemove);
        await vm.Downloads[0].RemoveAsync();
        Assert.Empty(vm.Downloads);
        Assert.False(vm.HasDownloads);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Downloads);
        Assert.Empty(service.Tasks);
        Assert.Empty(await fixture.Store.ReadTasksAsync(TestContext.Current.CancellationToken));
        using var restartedService = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var restarted = fixture.Create(restartedService);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(restarted.Downloads);
    }

    [AvaloniaFact]
    public async Task RemovingAwaitingAuthorizationCancelsAndDeletesWithoutAnExtraCancelClick()
    {
        using var fixture = new Fixture();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.WorkshopInput = "101"; await vm.LookupWorkshopAsync();
        vm.DownloadWorkshopCommand.Execute(null);
        await fixture.Provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await PumpUntil(() => vm.Downloads.FirstOrDefault()?.HasQrCode == true);
        var task = Assert.Single(service.Tasks);
        var row = Assert.Single(vm.Downloads);
        Assert.True(row.RemoveCommand.CanExecute(null));
        await row.RemoveAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WorkshopDownloadState.Cancelled, task.Snapshot.State);
        Assert.Empty(vm.Downloads);
        Assert.Empty(service.Tasks);
        Assert.Empty(await fixture.Store.ReadTasksAsync(TestContext.Current.CancellationToken));
        Assert.Empty(vm.InstalledMods);
    }

    [AvaloniaFact]
    public async Task CompletedDownloadsMergeRefreshesAndKeepUnsavedSettingsAndProfileDraft()
    {
        using var fixture = new Fixture();
        var scanner = new HoldingScanner(); fixture.Scanner = scanner;
        fixture.Provider.Release.TrySetResult();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile(); vm.ProfileName = "未保存的战役";
        vm.UserPath = "尚未保存的路径草稿";
        fixture.FailProfileLoad = true;
        var first = service.Start(await fixture.GetItemAsync(101), vm.UserDirectoryText);
        await first.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await PumpUntil(() => scanner.ScanStarted.Task.IsCompleted);
        var second = service.Start(await fixture.GetItemAsync(202), vm.UserDirectoryText);
        await second.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        scanner.Release.TrySetResult();
        await PumpUntil(() => vm.InstalledMods.Count == 2);
        Assert.Equal("未保存的战役", vm.ProfileName);
        Assert.Equal("尚未保存的路径草稿", vm.UserPath);
        Assert.Equal(new ulong?[] { 101, 202 }, vm.InstalledMods.Select(row => row.Mod.WorkshopId).Order());
    }

    [AvaloniaFact]
    public async Task CurrentInstallationReflectsDeleteRestartAndReinstallWithoutLosingHistory()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(token);
        var task = service.Start(await fixture.GetItemAsync(101, token), vm.UserDirectoryText);
        await task.Completion.WaitAsync(token);
        await PumpUntil(() => vm.Downloads.FirstOrDefault()?.HasInstalledPath == true);
        var row = Assert.Single(vm.Downloads);
        var path = row.InstalledPath;
        Assert.Equal("当前已安装", row.InstallationStatus);

        vm.SelectedInstalledMod = Assert.Single(vm.InstalledMods);
        Assert.Equal(ModInstallManagement.Application, vm.SelectedInstalledMod.Mod.Management);
        vm.PrepareInstalledModRemovalCommand.Execute(null);
        await vm.DeleteInstalledModAsync();
        Assert.False(Directory.Exists(path));
        Assert.Empty(vm.InstalledMods);
        Assert.Same(row, Assert.Single(vm.Downloads));
        Assert.Equal("下载完成", row.StateText);
        Assert.Equal("当前未安装", row.InstallationStatus);
        Assert.False(row.OpenDirectoryCommand.CanExecute(null));
        Assert.Equal(WorkshopDownloadState.Completed, Assert.Single(await fixture.Store.ReadTasksAsync(token)).State);

        using var restartedService = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var restarted = fixture.Create(restartedService);
        await restarted.InitializeAsync(token);
        var restored = Assert.Single(restarted.Downloads);
        Assert.Equal("下载完成", restored.StateText);
        Assert.Equal("当前未安装", restored.InstallationStatus);
        Assert.Equal("重新下载", restored.RetryText);
        Assert.False(restored.OpenDirectoryCommand.CanExecute(null));

        var screenshots = Environment.GetEnvironmentVariable("CK3_DOWNLOAD_SCREENSHOT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(screenshots))
        {
            restarted.Section = "downloads";
            var window = new MainWindow { DataContext = restarted };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(Path.Combine(screenshots, "download-history-after-removal.png"),
                    Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }

        var item = await fixture.GetItemAsync(101, token);
        var retry = restartedService.Retry(restored.Snapshot, item, restarted.UserDirectoryText);
        await retry.Completion.WaitAsync(token);
        await PumpUntil(() => restored.HasInstalledPath);
        Assert.Equal("下载完成", restored.StateText);
        Assert.Equal("当前已安装", restored.InstallationStatus);
        Assert.Equal("检查并更新", restored.RetryText);
        Assert.Equal(path, restored.InstalledPath);
        Assert.True(restored.OpenDirectoryCommand.CanExecute(null));
        Assert.Equal(task.Snapshot.TaskId, Assert.Single(await fixture.Store.ReadTasksAsync(token)).TaskId);
    }

    [AvaloniaFact]
    public async Task DownloadBadgeUsesTheInstalledDeclarationAndRendersInGreenAndRed()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        using var service = new WorkshopDownloadService(fixture.Provider, fixture.Store);
        using var vm = fixture.Create(service);
        await vm.InitializeAsync(token);
        var item = await fixture.GetItemAsync(101, token);
        var task = service.Start(item, vm.UserDirectoryText);
        await task.Completion.WaitAsync(token);
        await PumpUntil(() => vm.Downloads.FirstOrDefault()?.HasInstalledPath == true);
        var row = Assert.Single(vm.Downloads);
        Assert.Equal("兼容", row.Compatibility);

        vm.Section = "downloads";
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Assert.Equal(Color.Parse("#2F9E6E"), BadgeColor("兼容"));

            // 本地安装声明应优先于本次下载保存的 1.20 工坊标签。
            await File.WriteAllTextAsync(Path.Combine(row.InstalledPath, "descriptor.mod"),
                """name="应用内下载" version="1.1" supported_version="1.19.*" """, token);
            await vm.RefreshAsync();
            Assert.Equal("不兼容", row.Compatibility);
            Assert.Equal(Color.Parse("#D9453A"), BadgeColor("不兼容"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                label => label.IsEffectivelyVisible && label.Text == "工坊 101");

            var screenshots = Environment.GetEnvironmentVariable("CK3_DOWNLOAD_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(screenshots))
                window.CaptureRenderedFrame()?.Save(Path.Combine(screenshots, "download-compatibility.png"),
                    Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }

        Color BadgeColor(string text)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var label = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                item => item.IsEffectivelyVisible && item.Text == text && item.Classes.Contains("recommendation"));
            return Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color;
        }
    }

    [AvaloniaFact]
    public void UnknownTotalNeverShowsFabricatedPercentageAndCancelledTaskIsNotInstalled()
    {
        var snapshot = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "未知大小", WorkshopDownloadState.Downloading,
            12, null, DateTimeOffset.UtcNow);
        using var row = new DownloadRow(snapshot, _ => { }, _ => { });
        Assert.True(row.IsIndeterminate);
        Assert.DoesNotContain("%", row.ProgressText);
        row.Update(snapshot with { State = WorkshopDownloadState.Cancelled, UpdatedAt = snapshot.UpdatedAt.AddSeconds(1) });
        Assert.False(row.HasInstalledPath);
        Assert.False(row.CanCancel);
        Assert.Equal("已取消", row.StateText);
    }

    private static async Task PumpUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(predicate());
    }

    private sealed class Fixture : IGameLocator, ISettingsStore, IProfileStore, IWorkshopSource, IExternalNavigator, IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ck3-ui-download-").FullName;
        public Provider Provider { get; } = new();
        public IModScanner? Scanner { get; set; }
        public bool FailProfileLoad { get; set; }
        public bool FailProfileSave { get; set; }
        public FileWorkshopDownloadStore Store => new(Path.Combine(_root, "data"));
        public int OpenWorkshopCalls;
        public string? OpenedDirectory;
        public MainWindowViewModel Create(WorkshopDownloadService service, IWorkshopDependencySource? dependencySource = null) => new(
            new WorkspaceService(this, Scanner ?? new Ck3ModScanner(store: Store), this, this,
                installedModRemover: new InstalledModRemover(Ck3.AppId, Store, null),
                installedModContent: new FileInstalledModContent()), new WorkshopService(this), this, downloads: service,
            dependencySource: dependencySource);
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, Path.Combine(_root, "user"), "1.20.1", [], []));
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => FailProfileLoad
            ? Task.FromException<IReadOnlyList<ModProfile>>(new InvalidDataException("测试：配置无法读取"))
            : Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => FailProfileSave
            ? Task.FromException(new IOException("测试：配置无法保存")) : Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default) => Task.FromResult(
            new WorkshopItem(publishedFileId, Ck3.AppId, "下载测试", "完整作者介绍", null,
                new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=101"), DateTimeOffset.UtcNow, 4, ["1.20"], null));
        public void OpenWorkshop(ulong? publishedFileId = null) => OpenWorkshopCalls++;
        public void OpenDirectory(string path) => OpenedDirectory = path;
        public void LaunchGame() { }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class HoldingScanner : IModScanner
    {
        private int _scans;
        public TaskCompletionSource ScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModScanResult> ScanAsync(string directory, IReadOnlyList<string> workshop, CancellationToken token = default)
        {
            var snapshot = await new Ck3ModScanner().ScanAsync(directory, workshop, token);
            if (Interlocked.Increment(ref _scans) == 2)
            { ScanStarted.TrySetResult(); await Release.Task.WaitAsync(token); }
            return snapshot;
        }
    }

    public sealed class Provider : IWorkshopContentProvider
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("name=\"应用内下载\"\nversion=\"1.0\"\nsupported_version=\"1.20.*\"\n");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkshopContentPlan> ResolveAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        {
            if (!Release.Task.IsCompleted)
            {
                progress.Report(new(WorkshopDownloadState.AwaitingAuthentication, AuthenticationUrl: "https://s.team/q/1/test"));
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(item.ConsumerAppId, item.PublishedFileId, "test-revision", [new("descriptor.mod", Content.Length, Convert.ToHexString(SHA1.HashData(Content)))]);
        }
        public async Task DownloadAsync(WorkshopContentPlan plan, string stagingDirectory, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(stagingDirectory);
            progress.Report(new(WorkshopDownloadState.Downloading, Content.Length / 2, Content.Length));
            await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "descriptor.mod"), Content, cancellationToken);
            progress.Report(new(WorkshopDownloadState.Downloading, Content.Length, Content.Length));
        }
    }
}
