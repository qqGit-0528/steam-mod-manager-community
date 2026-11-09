using Ck3ModManager.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

[assembly: AvaloniaTestApplication(typeof(Ck3ModManager.Desktop.Tests.TestAppBuilder))]

namespace Ck3ModManager.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopTests
{
    [Fact]
    public void LaunchGameRequestsSteamWithoutRequiringLocalDiscovery()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        Assert.True(vm.LaunchGameCommand.CanExecute(null));

        vm.LaunchGameCommand.Execute(null);

        Assert.Equal(1, fixture.Navigator.LaunchRequests);
        Assert.Contains("已请求 Steam 启动 CK3", vm.Status);
        Assert.False(vm.HasError);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public void LaunchFailureIsReportedAndSuccessfulRetryClearsIt()
    {
        var fixture = new Fixture();
        var failure = new InvalidOperationException("没有 Steam 协议处理程序");
        fixture.Navigator.Failure = failure;
        using var vm = fixture.Create();

        vm.LaunchGameCommand.Execute(null);

        Assert.Same(failure, vm.LastError);
        Assert.Contains("请检查 Steam 是否已安装", vm.ErrorMessage);
        Assert.Contains(failure.Message, vm.ErrorMessage);
        Assert.Contains("未完成", vm.Status);

        fixture.Navigator.Failure = null;
        vm.LaunchGameCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Null(vm.LastError);
        Assert.Equal(1, fixture.Navigator.LaunchRequests);
        Assert.Contains("已请求 Steam 启动 CK3", vm.Status);
    }

    [AvaloniaFact]
    public async Task WorkshopTagsResolveCompatibilityForUndeclaredSteamMod()
    {
        var fixture = new Fixture();
        fixture.Scanner.Mods = [new InstalledMod("w", "无声明 Mod", "test/w", "test/w.mod", "1", null, 42, [], ModOrigin.SteamWorkshop, true)
            { Management = ModInstallManagement.Steam }];
        using var vm = fixture.Create();

        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.CompatibilityLookupTask;
        Dispatcher.UIThread.RunJobs();

        var row = Assert.Single(vm.InstalledMods);
        Assert.Equal("兼容", row.Compatibility);
        Assert.True(row.IsCompatibilityMatched);
    }

    [AvaloniaFact]
    public async Task WorkshopLookupFailureKeepsUndeclaredModUnknown()
    {
        var fixture = new Fixture();
        fixture.Scanner.Mods = [new InstalledMod("w", "无声明 Mod", "test/w", "test/w.mod", "1", null, 42, [], ModOrigin.SteamWorkshop, true)
            { Management = ModInstallManagement.Steam }];
        fixture.Source.Failure = new InvalidOperationException("工坊不可用");
        using var vm = fixture.Create();

        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.CompatibilityLookupTask;
        Dispatcher.UIThread.RunJobs();

        var row = Assert.Single(vm.InstalledMods);
        Assert.Equal("未知", row.Compatibility);
    }

    [Fact]
    public async Task ProfileSaveKeepsUserOrderAndRejectsDuplicateAdds()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "北境战役";
        vm.SelectedInstalledMod = vm.InstalledMods[0];
        vm.AddSelectedMod();
        vm.AddSelectedMod();
        vm.SelectedInstalledMod = vm.InstalledMods[1];
        vm.AddSelectedMod();
        vm.MoveSelectedMod(-1);
        await vm.SaveProfileAsync();
        var saved = Assert.Single(fixture.Profiles.Items);
        Assert.Equal("北境战役", saved.Name);
        Assert.Equal(new[] { "b", "a" }, saved.Mods.Select(m => m.InstalledModId));
        Assert.False(vm.IsProfileDirty);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task RefreshDoesNotDiscardUnsavedProfileOrOrder()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "还没保存的战役";
        vm.SelectedInstalledMod = vm.InstalledMods[1];
        vm.AddSelectedMod();
        await vm.RefreshAsync();
        Assert.Equal("还没保存的战役", vm.ProfileName);
        Assert.Equal("b", Assert.Single(vm.ProfileMods).Mod.InstalledModId);
        Assert.True(vm.IsProfileDirty);
        Assert.Empty(fixture.Profiles.Items);
    }

    [Fact]
    public async Task DeleteProfileRequiresSecondClickAndRemovesSavedProfile()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "待删除";
        await vm.SaveProfileAsync();
        Assert.Single(fixture.Profiles.Items);

        await vm.DeleteProfileAsync();

        Assert.Single(fixture.Profiles.Items);
        Assert.Contains("再次点击", vm.Status);
        Assert.Equal("再次点击确认删除", vm.DeleteProfileLabel);

        await vm.DeleteProfileAsync();

        Assert.Empty(fixture.Profiles.Items);
        Assert.Empty(vm.Profiles);
        Assert.Null(vm.SelectedProfile);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("删除选中配置", vm.DeleteProfileLabel);
    }

    [Fact]
    public async Task DeletingUnsavedProfileDiscardsDraftWithoutSavingIt()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "未保存草稿";

        await vm.DeleteProfileAsync();
        await vm.DeleteProfileAsync();

        Assert.Empty(vm.Profiles);
        Assert.Null(vm.SelectedProfile);
        Assert.Empty(fixture.Profiles.Items);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task SwitchingProfileCancelsPendingDeletion()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "第一份";
        await vm.SaveProfileAsync();
        var first = vm.SelectedProfile!;
        vm.CreateProfile();
        vm.ProfileName = "第二份";
        await vm.SaveProfileAsync();
        var second = vm.SelectedProfile!;
        Assert.NotEqual(first.Id, second.Id);

        await vm.DeleteProfileAsync();
        Assert.Equal("再次点击确认删除", vm.DeleteProfileLabel);

        vm.SelectedProfile = first;
        Assert.Equal("删除选中配置", vm.DeleteProfileLabel);

        await vm.DeleteProfileAsync();
        Assert.Equal(2, vm.Profiles.Count);
    }

    [AvaloniaFact]
    public async Task ProfilePageExposesDeleteButtonWithConfirmationLabel()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.CreateProfile();
        vm.ProfileName = "界面删除";
        await vm.SaveProfileAsync();
        vm.Section = "profiles";

        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Classes.Contains("danger") && ReferenceEquals(b.Command, vm.DeleteProfileCommand));
            Assert.Equal("删除选中配置", button.Content);
            var rowButton = Assert.Single(window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("rowdelete")));
            Assert.IsAssignableFrom<ModProfile>(rowButton.CommandParameter);
            Assert.True(rowButton.Command!.CanExecute(rowButton.CommandParameter));

            button.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("再次点击确认删除", button.Content);
            Assert.Single(vm.Profiles);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task FailedDetailRefreshKeepsPreviousItemAndDisablesSubscription()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "2273832430";
        await vm.LookupWorkshopAsync();
        Assert.True(vm.HasWorkshopItem);
        Assert.Contains("依赖待确认", vm.WorkshopDependencies);
        var original = vm.WorkshopItem;
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));
        fixture.Source.Failure = new HttpRequestException("测试网络故障");
        vm.RefreshWorkshopCommand.Execute(null);
        await vm.WorkshopLoadingTask;
        Assert.True(vm.HasWorkshopItem);
        Assert.Equal(original, vm.WorkshopItem);
        Assert.True(vm.HasError);
        Assert.Contains("测试网络故障", vm.ErrorMessage);
        Assert.Contains("保留已有资料", vm.WorkshopDataStatus);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.False(vm.CanDownloadWorkshopItem);
        Assert.Null(vm.LastError);
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
    }

    [Fact]
    public async Task CancelStopsLookupAndReleasesBusyState()
    {
        var fixture = new Fixture();
        fixture.Source.WaitForCancellation = true;
        using var vm = fixture.Create();
        vm.WorkshopInput = "2273832430";
        var operation = vm.LookupWorkshopAsync();
        await fixture.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.IsBusy);
        Assert.True(vm.WorkshopWorking);
        Assert.True(vm.CanCancelOperation);
        vm.CancelCurrentOperation();
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
        Assert.False(vm.CanCancelOperation);
        Assert.Null(vm.LastError);
        Assert.Equal("操作已取消", vm.Status);
    }

    [AvaloniaFact]
    public async Task WindowLoadsAndWorkshopBindingsRender()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var launchButton = window.FindControl<Button>("LaunchGameButton")!;
            Assert.Same(vm.LaunchGameCommand, launchButton.Command);
            Assert.True(launchButton.IsEffectivelyEnabled);
            vm.NavigateCommand.Execute("workshop");
            vm.WorkshopInput = "2273832430";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2273832430", window.FindControl<TextBox>("WorkshopReferenceInput")!.Text);
            Assert.True(window.FindControl<Button>("WorkshopLookupButton")!.IsEffectivelyEnabled);
            await vm.LookupWorkshopAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("测试工坊条目", vm.WorkshopTitle);

            var screenshotPath = Environment.GetEnvironmentVariable("CK3_UI_SCREENSHOT_PATH");
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                vm.NavigateCommand.Execute("overview");
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(screenshotPath, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InstalledLibraryDeletionRequiresSeparateConfirmationAndRefreshesFromDisk()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("mod-delete-ui-").FullName;
        try
        {
            var mods = Directory.CreateDirectory(Path.Combine(root, "mod")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(mods, "target")).FullName;
            var wrapper = Path.Combine(mods, "target.mod");
            await File.WriteAllTextAsync(wrapper, "name=\"测试删除 Mod\" path=\"mod/target\"", token);
            await File.WriteAllTextAsync(Path.Combine(target, "payload.txt"), "on disk", token);
            var workspace = new WorkspaceService(new Locator(root), new Ck3ModScanner(), new ProfileMemory(),
                new SettingsMemory(), installedModRemover: new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(root), null));
            using var vm = new MainWindowViewModel(workspace, new WorkshopService(new WorkshopMemory()), new Navigator());
            await vm.InitializeAsync(token);
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                vm.SelectedInstalledMod = Assert.Single(vm.InstalledMods);
                Dispatcher.UIThread.RunJobs();
                var prepare = window.FindControl<Button>("PrepareInstalledModRemovalButton")!;
                var confirm = window.FindControl<Button>("ConfirmInstalledModRemovalButton")!;
                Assert.Same(vm.PrepareInstalledModRemovalCommand, prepare.Command);
                Assert.Same(vm.DeleteInstalledModCommand, confirm.Command);
                Assert.False(confirm.Command!.CanExecute(null));
                prepare.Command!.Execute(null);
                Assert.True(vm.HasPendingInstalledModRemoval);
                Assert.Contains(target, vm.InstalledModRemovalPrompt);
                Assert.False(prepare.Command.CanExecute(null));
                Assert.True(Directory.Exists(target));
                vm.CancelInstalledModRemovalCommand.Execute(null);
                Assert.False(confirm.Command.CanExecute(null));
                Assert.True(Directory.Exists(target));

                prepare.Command.Execute(null);
                var screenshot = Environment.GetEnvironmentVariable("CK3_MOD_REMOVAL_SCREENSHOT_PATH");
                if (!string.IsNullOrWhiteSpace(screenshot))
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame()?.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                await vm.DeleteInstalledModAsync();
                Assert.False(Directory.Exists(target));
                Assert.False(File.Exists(wrapper));
                Assert.Empty(vm.InstalledMods);
                Assert.Null(vm.SelectedInstalledMod);
                Assert.Contains("已从磁盘删除", vm.Status);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task LibraryShowsSameWorkshopCopiesWithVersionSourceAndPaths()
    {
        var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var root = Path.GetTempPath();
        var steam = fixture.Scanner.Mods[0] with { ContentPath = Path.Combine(root, "steam"), Management = ModInstallManagement.Steam };
        var local = steam with { Id = "copy", ContentPath = Path.Combine(root, "local"), ModVersion = "old", Management = ModInstallManagement.Local };
        fixture.Scanner.Mods = [steam, local];
        using var vm = fixture.Create();
        await vm.InitializeAsync(token);

        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 930 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var labels = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Text == "同工坊副本").ToArray();
            Assert.Equal(2, labels.Length);
            Assert.All(labels, label =>
            {
                var tip = Assert.IsType<string>(ToolTip.GetTip(label));
                Assert.Contains(steam.ContentPath, tip);
                Assert.Contains(local.ContentPath, tip);
                Assert.Contains("STEAM", tip);
                Assert.Contains("本地  ·  Mod old", tip);
            });
            CaptureBatchPreview(window, "duplicate-copies.png");

            fixture.Scanner.Mods = [steam];
            await vm.RefreshAsync();
            Assert.False(Assert.Single(vm.InstalledMods).HasDuplicateCopies);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileDragMovesAcrossRowsAndEscapeCancels()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture();
        var original = Enumerable.Range(1, 4).Select(index => new ProfileMod($"mod-{index}", $"测试 Mod {index}",
            "", null, null)).ToArray();
        fixture.Profiles.Items.Add(new ModProfile(Guid.NewGuid(), "拖拽顺序测试", null, original, DateTimeOffset.UtcNow));
        using var vm = fixture.Create();
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        var window = new MainWindow { DataContext = vm, Width = 1220, Height = 960 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var list = window.FindControl<ListBox>("ProfileModList")!;
            Point Handle(string id)
            {
                var handle = Assert.Single(list.GetVisualDescendants().OfType<Border>(),
                    item => item.Name == "ProfileDragHandle" && item.DataContext is ProfileModRow row && row.Mod.InstalledModId == id);
                return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            }
            Point Target(string id, double heightFraction)
            {
                var row = Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>(),
                    item => item.DataContext is ProfileModRow mod && mod.Mod.InstalledModId == id);
                return row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height * heightFraction), window)!.Value;
            }

            var last = Target("mod-4", 0.8);
            window.MouseDown(Handle("mod-1"), MouseButton.Left);
            window.MouseMove(last, RawInputModifiers.LeftMouseButton);
            Assert.Contains(list.GetVisualDescendants().OfType<ListBoxItem>(), item => item.Classes.Contains("insertAfter"));
            CaptureBatchPreview(window, "drag-reorder.png");
            window.MouseUp(last, MouseButton.Left);
            Assert.Equal(new[] { "mod-2", "mod-3", "mod-4", "mod-1" }, vm.ProfileMods.Select(row => row.Mod.InstalledModId));
            Assert.Equal("mod-1", vm.SelectedProfileMod?.Mod.InstalledModId);
            Assert.True(vm.IsProfileDirty);

            window.UpdateLayout();
            var first = Target("mod-2", 0.2);
            window.MouseDown(Handle("mod-1"), MouseButton.Left);
            window.MouseMove(first, RawInputModifiers.LeftMouseButton);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.MouseUp(first, MouseButton.Left);
            Assert.Equal("mod-2", vm.ProfileMods[0].Mod.InstalledModId);

            window.MouseDown(Handle("mod-4"), MouseButton.Left);
            window.MouseMove(first, RawInputModifiers.LeftMouseButton);
            window.MouseUp(first, MouseButton.Left);
            await vm.SaveProfileAsync();
            Assert.Equal(new[] { "mod-4", "mod-2", "mod-3", "mod-1" }, Assert.Single(fixture.Profiles.Items).Mods.Select(mod => mod.InstalledModId));
            Assert.Equal(new[] { 1, 2, 3, 4 }, vm.ProfileMods.Select(row => row.Order));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileDragScrollsLongListsAndDroppingOutsideKeepsOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture();
        var mods = Enumerable.Range(1, 18).Select(index => new ProfileMod($"mod-{index}", $"测试 Mod {index}",
            "", null, null)).ToArray();
        fixture.Profiles.Items.Add(new ModProfile(Guid.NewGuid(), "拖拽滚动测试", null, mods, DateTimeOffset.UtcNow));
        using var vm = fixture.Create();
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        var window = new MainWindow { DataContext = vm, Width = 1220, Height = 960 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var list = window.FindControl<ListBox>("ProfileModList")!;
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var handle = Assert.Single(list.GetVisualDescendants().OfType<Border>(), item =>
                item.Name == "ProfileDragHandle" && item.DataContext is ProfileModRow { Order: 1 });
            var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            var edge = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 8), window)!.Value;

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (scroll.Offset.Y == 0) await Task.Delay(10, timeout.Token);

            window.MouseMove(new Point(10, 10), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(10, 10), MouseButton.Left);
            Assert.Equal(mods.Select(mod => mod.InstalledModId), vm.ProfileMods.Select(row => row.Mod.InstalledModId));
            Assert.False(vm.IsProfileDirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileFileConflictButtonChecksEnabledInstallationsAndShowsOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("file-conflict-ui-");
        try
        {
            var fixture = new Fixture();
            fixture.Scanner.Mods = Enumerable.Range(1, 3).Select(index =>
            {
                var path = root.CreateSubdirectory($"mod-{index}").FullName;
                var gui = Directory.CreateDirectory(Path.Combine(path, "gui"));
                File.WriteAllText(Path.Combine(gui.FullName, "frontend_main.gui"), $"marker {index}");
                var traits = Directory.CreateDirectory(Path.Combine(path, "common", "traits"));
                File.WriteAllText(Path.Combine(traits.FullName, $"traits_{index}.txt"), "shared_trait = { }");
                return new InstalledMod($"mod-{index}", $"测试 Mod {index}", path, "", "1.0", "1.20.*",
                    null, [], ModOrigin.Local, true);
            }).ToArray();
            var mods = fixture.Scanner.Mods.Select((mod, index) => new ProfileMod(mod.Id, mod.Name, mod.ContentPath, null, mod.SupportedGameVersion)
                { Enabled = index != 1 }).ToArray();
            var missing = mods[0] with { InstalledModId = "missing", DisplayName = "已移除 Mod" };
            fixture.Profiles.Items.Add(new ModProfile(Guid.NewGuid(), "文件冲突测试", null, [.. mods, missing], DateTimeOffset.UtcNow));
            using var vm = fixture.Create(new Ck3FileConflictScanner());
            await vm.InitializeAsync(token);
            vm.Section = "profiles";
            var window = new MainWindow { DataContext = vm, Width = 1220, Height = 960 };
            try
            {
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var check = window.FindControl<Button>("CheckFileConflictsButton")!;
                Assert.True(check.IsEffectivelyVisible && check.IsEffectivelyEnabled);
                check.Command!.Execute(null);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (vm.IsBusy) await Task.Delay(10, timeout.Token);
                var row = Assert.Single(vm.FileConflictRows);
                Assert.Equal("gui/frontend_main.gui", row.Path);
                Assert.Equal("内容不同", row.State);
                Assert.Equal("#1 测试 Mod 1 → #3 测试 Mod 3", row.LoadOrder);
                Assert.Contains("已移除 Mod", Assert.Single(vm.FileConflictWarnings));
                Assert.True(vm.HasFileConflictReport);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == row.Path);
                CaptureBatchPreview(window, "file-conflict-report.png");
                var definition = Assert.Single(vm.DefinitionConflictRows);
                Assert.Equal("shared_trait", definition.Key);
                Assert.Contains("traits_1.txt", definition.Sources);
                Assert.DoesNotContain("traits_2.txt", definition.Sources);
                Assert.Contains("traits_3.txt", definition.Sources);
                var secondBatch = Environment.GetEnvironmentVariable("CK3_BATCH2_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(secondBatch))
                    window.CaptureRenderedFrame()!.Save(Path.Combine(secondBatch, "trait-conflicts.png"),
                        Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.ProfileMods[0].Enabled = false;
                Assert.False(vm.HasFileConflictReport);
            }
            finally { window.Close(); }
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task SwitchingWorkspacesDiscardsAnInFlightFileConflictReport()
    {
        var fixture = new Fixture();
        var scanner = new DelayedConflictScanner();
        using var vm = fixture.Create(scanner);
        var token = TestContext.Current.CancellationToken;
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        vm.CreateProfile();
        vm.SelectedInstalledMod = vm.InstalledMods[0];
        vm.AddSelectedMod();
        var check = vm.CheckFileConflictsAsync();
        await scanner.Started.Task.WaitAsync(token);

        vm.SetLocalModMonitoringEnabled(false);
        Assert.True(scanner.Token.IsCancellationRequested);
        vm.SetLocalModMonitoringEnabled(true);
        scanner.Result.SetResult(new(1, [], []));
        await check.WaitAsync(token);
        Assert.False(vm.HasFileConflictReport);
    }

    private static void CaptureBatchPreview(Window window, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("CK3_BATCH1_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(directory))
            window.CaptureRenderedFrame()!.Save(Path.Combine(directory, fileName),
                Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class DelayedConflictScanner : IFileConflictScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FileConflictReport> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public Task<FileConflictReport> CheckAsync(IReadOnlyList<FileConflictMod> mods, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.SetResult();
            return Result.Task;
        }
    }

    private sealed class Fixture
    {
        public ProfileMemory Profiles { get; } = new();
        public WorkshopMemory Source { get; } = new();
        public Scanner Scanner { get; } = new();
        public Navigator Navigator { get; } = new();
        public MainWindowViewModel Create(IFileConflictScanner? fileConflicts = null) =>
            new(new WorkspaceService(new Locator(), Scanner, Profiles, new SettingsMemory(), fileConflicts: fileConflicts),
                new WorkshopService(Source), Navigator) { WorkshopTargetVersion = "1.20.1" };
    }

    private sealed class ProfileMemory : IProfileStore
    {
        public List<ModProfile> Items { get; } = [];
        public Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ModProfile>>(Items.ToArray());
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
        { Items.RemoveAll(p => p.Id == profile.Id); Items.Add(profile); return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        { Items.RemoveAll(p => p.Id == id); return Task.CompletedTask; }
    }
    private sealed class SettingsMemory : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Locator(string userData = "test-user-data") : IGameLocator
    {
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, userData, "1.20.1", [], ["测试环境，没有安装游戏"]));
    }
    private sealed class Scanner : IModScanner
    {
        public IReadOnlyList<InstalledMod> Mods { get; set; } = [
            new InstalledMod("a", "测试 Mod A", "test/a", "test/a.mod", "1", "1.20.*", 1, [], ModOrigin.SteamWorkshop, true),
            new InstalledMod("b", "测试 Mod B", "test/b", "test/b.mod", "2", "1.19.*", 2, [], ModOrigin.SteamWorkshop, true)
        ];
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModScanResult(Mods, []));
    }
    private sealed class WorkshopMemory : IWorkshopSource
    {
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Failure is not null) throw Failure;
            return new WorkshopItem(publishedFileId, Ck3.AppId, "测试工坊条目", "测试描述", null,
                new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}"), DateTimeOffset.UtcNow, 1, ["1.20"], null);
        }
    }
    private sealed class Navigator : IExternalNavigator
    {
        public int LaunchRequests { get; private set; }
        public Exception? Failure { get; set; }
        public void LaunchGame()
        {
            if (Failure is not null) throw Failure;
            LaunchRequests++;
        }
        public void OpenWorkshop(ulong? publishedFileId = null) { }
        public void OpenDirectory(string path) { }
    }
}
