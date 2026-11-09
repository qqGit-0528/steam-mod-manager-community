using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class GameActivationTests
{
    [Fact]
    public async Task BothListsEditOneProfileAndSavingDoesNotApplyIt()
    {
        var source = new Source();
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.GameProfileApplied);
        Assert.True(vm.InstalledMods[0].Enabled);
        Assert.False(vm.InstalledMods[1].Enabled);

        vm.InstalledMods[1].Enabled = true;
        Assert.True(vm.ProfileMods[1].Enabled);
        Assert.False(vm.GameProfileApplied);
        vm.ProfileMods[0].Enabled = false;
        Assert.False(vm.InstalledMods[0].Enabled);
        vm.SelectedProfileMod = vm.ProfileMods[1];
        vm.MoveSelectedMod(-1);
        await vm.SaveProfileAsync();
        Assert.Equal(0, source.ApplyCalls);
        Assert.False(vm.GameProfileApplied);
        Assert.Equal(new[] { ("b", true), ("a", false) }, source.Saved!.Mods.Select(mod => (mod.InstalledModId, mod.Enabled)));

        await vm.ApplyGameProfileAsync();
        Assert.Equal(1, source.ApplyCalls);
        Assert.True(vm.GameProfileApplied);
        Assert.False(vm.IsProfileDirty);
        Assert.Equal(source.Saved!.Mods, source.Applied!.Mods);
        Assert.Contains("下次启动游戏生效", vm.Status);
    }

    [Fact]
    public async Task FailedApplyKeepsTheDraftAndMissingEnabledModsCannotReachTheWriter()
    {
        var source = new Source { FailApply = true };
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.ProfileMods[1].Enabled = true;
        await vm.ApplyGameProfileAsync();
        Assert.True(vm.HasError);
        Assert.False(vm.GameProfileApplied);
        Assert.True(vm.IsProfileDirty);
        Assert.Null(source.Saved);
        source.FailApply = false;
        source.MissingB = true;
        await vm.ApplyGameProfileAsync();
        Assert.Equal(1, source.ApplyCalls);
        Assert.Contains("文件缺失", vm.ErrorMessage);
        vm.ProfileMods[1].Enabled = false;
        await vm.ApplyGameProfileAsync();
        Assert.False(vm.HasError);
        Assert.True(vm.GameProfileApplied);
    }

    [AvaloniaFact]
    public async Task PowerSwitchesAndApplyButtonsRenderAndStayBoundAcrossBothPages()
    {
        var source = new Source();
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 800 };
        var screenshots = Environment.GetEnvironmentVariable("CK3_ACTIVATION_SCREENSHOT_DIRECTORY");
        try
        {
            GameTheme.Apply(GameCatalog.Ck3);
            window.Show(); Dispatcher.UIThread.RunJobs();
            var local = window.GetVisualDescendants().OfType<ToggleButton>().First(button => button.Classes.Contains("modActivation") && button.DataContext == vm.InstalledMods[1]);
            Assert.False(local.IsChecked);
            local.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ProfileMods[1].Enabled);
            Assert.Same(vm.ApplyGameProfileCommand, window.FindControl<Button>("ApplyLocalGameProfileButton")!.Command);
            if (!string.IsNullOrWhiteSpace(screenshots))
            {
                Directory.CreateDirectory(screenshots);
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "activation-local.png"), PngBitmapEncoderOptions.Default);
            }
            vm.Section = "profiles"; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var configured = window.GetVisualDescendants().OfType<ToggleButton>().First(button => button.Classes.Contains("modActivation") && button.DataContext == vm.ProfileMods[1]);
            Assert.True(configured.IsChecked);
            configured.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.InstalledMods[1].Enabled);
            Assert.Same(vm.ApplyGameProfileCommand, window.FindControl<Button>("ApplyGameProfileButton")!.Command);
            if (!string.IsNullOrWhiteSpace(screenshots))
            {
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "activation-profile.png"), PngBitmapEncoderOptions.Default);
                window.Width = 1000; window.Height = 690; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "activation-profile-minimum.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task CancellingVerificationNeverLaunchesEvenWhenTheProfileWasAlreadyApplied()
    {
        var source = new Source();
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.GameProfileApplied);
        source.WaitForReadCancellation = true;
        var launch = vm.LaunchGameWithProfileAsync();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        vm.CancelCurrentOperation();
        await launch.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, source.LaunchCalls);
        Assert.Equal(0, source.ApplyCalls);
        Assert.Equal("操作已取消", vm.Status);
    }

    [Fact]
    public async Task MissingContentIsRecheckedBeforeLaunchingAnAlreadyAppliedProfile()
    {
        var source = new Source();
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.ProfileMods[1].Enabled = true;
        await vm.ApplyGameProfileAsync();
        source.MissingB = true;
        await vm.LaunchGameWithProfileAsync();
        Assert.Equal(0, source.LaunchCalls);
        Assert.Contains("文件缺失", vm.ErrorMessage);
    }

    [Fact]
    public async Task LaunchRechecksNativeChangesEvenAfterThisToolHasAppliedSuccessfully()
    {
        var source = new Source();
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.ApplyGameProfileAsync();
        Assert.True(vm.GameProfileApplied);
        source.ExternalChanged = true;
        await vm.LaunchGameWithProfileAsync();
        Assert.Equal(2, source.ApplyCalls);
        Assert.Equal(1, source.LaunchCalls);
        Assert.True(source.Applied!.Mods[0].Enabled);
        Assert.False(source.Applied.Mods[1].Enabled);
    }

    [Fact]
    public async Task LaunchAppliesPendingSwitchesAndDoesNotLaunchAfterAnApplyFailure()
    {
        var source = new Source { FailApply = true };
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.ProfileMods[1].Enabled = true;
        await vm.LaunchGameWithProfileAsync();
        Assert.Equal(0, source.LaunchCalls);
        source.FailApply = false;
        await vm.LaunchGameWithProfileAsync();
        Assert.Equal(1, source.LaunchCalls);
        Assert.True(vm.GameProfileApplied);
        Assert.True(source.Applied!.Mods[1].Enabled);
    }

    [Fact]
    public async Task LaunchStopsWhenLauncherReadbackHasTheWrongOrderAfterApplying()
    {
        var source = new Source { ReverseAppliedOrder = true };
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.ProfileMods[1].Enabled = true;

        await vm.LaunchGameWithProfileAsync();

        Assert.Equal(1, source.ApplyCalls);
        Assert.Equal(0, source.LaunchCalls);
        Assert.False(vm.GameProfileApplied);
        Assert.Contains("加载顺序与当前配置不一致", vm.ErrorMessage);
    }

    [Fact]
    public async Task LaunchKeepsVerifiedLauncherStateButStopsWhenSavingTheProfileFails()
    {
        var source = new Source { FailProfileSave = true };
        using var vm = source.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.ProfileMods[1].Enabled = true;

        await vm.LaunchGameWithProfileAsync();

        Assert.Equal(1, source.ApplyCalls);
        Assert.Equal(0, source.LaunchCalls);
        Assert.True(vm.GameProfileApplied);
        Assert.Null(source.Saved);
        Assert.Contains("已应用到启动器，但本工具保存失败", vm.ErrorMessage);
    }

    [Fact]
    public void ProfileRowsExposeDeclaredCompatibility()
    {
        var mod = new ProfileMod("m", "Mod", "/mods/m", null, "1.20.*");
        Assert.True(new ProfileModRow(mod, 1, "1.20.0.4").IsCompatibilityMatched);
        Assert.True(new ProfileModRow(mod with { SupportedGameVersion = "1.21.*" }, 1, "1.20.0.4").IsCompatibilityMismatch);
        Assert.True(new ProfileModRow(mod with { SupportedGameVersion = null }, 1, "1.20.0.4").IsCompatibilityUnknown);
    }

    [AvaloniaFact]
    public async Task SaveBindingUsesTheChosenSnapshotAndRestoresWithoutApplying()
    {
        var source = new Source();
        var snapshots = new SnapshotStore { ShowDifference = true };
        var saves = new SaveBindingStore();
        using var vm = source.Create(snapshots, saves);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.Section = "profiles";
        vm.CapturePlaysetSnapshotCommand.Execute(null);
        await vm.SelectCk3SaveAsync("test.ck3");
        Assert.Contains("未知", vm.SaveMetadataText);
        Assert.True(vm.BindCk3SaveCommand.CanExecute(null));
        vm.BindCk3SaveCommand.Execute(null);
        vm.CheckCk3SaveCommand.Execute(null);
        Assert.Contains("Mod 文件不同", vm.SaveCheckMessages);
        Assert.True(vm.RestoreBoundSaveCommand.CanExecute(null));
        vm.RestoreBoundSaveCommand.Execute(null);
        Assert.Equal(saves.Binding!.SnapshotId, vm.SelectedProfile!.Id);
        Assert.Equal(0, source.ApplyCalls);
        Assert.Empty(vm.SaveCheckMessages);
    }

    [AvaloniaFact]
    public async Task BatchCheckDiscardsResultsWhenTheSelectedConfigurationChanges()
    {
        var updates = new DelayedWorkshopStatus();
        var source = new Source();
        using var vm = source.Create(updates: updates);
        var token = TestContext.Current.CancellationToken;
        await vm.InitializeAsync(token);
        vm.Section = "profiles";
        vm.ProfileMods[1].Enabled = true;
        var original = vm.SelectedProfile!;
        var running = vm.CheckWorkshopUpdatesAsync();
        await updates.Started.Task.WaitAsync(token);

        vm.SelectedProfile = original with { Id = Guid.NewGuid(), Name = "另一份配置" };
        updates.Response.SetResult(new(new Dictionary<ulong, WorkshopUpdateState> { [123] = WorkshopUpdateState.Processing }));
        await running;
        Assert.Empty(vm.WorkshopUpdateRows);
        Assert.False(vm.HasWorkshopUpdateReport);

        vm.SelectedProfile = original;
        await vm.CheckWorkshopUpdatesAsync();
        Assert.Equal("Steam 正在处理", Assert.Single(vm.WorkshopUpdateRows).Status);
        vm.ProfileMods[1].Enabled = false;
        Assert.False(vm.HasWorkshopUpdateReport);
    }

    private sealed class DelayedWorkshopStatus : IWorkshopStatusSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkshopStatusCheck> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkshopStatusCheck> CheckAsync(GameEnvironment environment, IReadOnlyList<ulong> itemIds,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(123UL, Assert.Single(itemIds));
            Started.TrySetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SaveBindingStore : ICk3SaveBindingStore
    {
        private static readonly Ck3SaveStamp Stamp = new(2, DateTime.UnixEpoch);
        public Ck3SaveBinding? Binding { get; private set; }

        public Task<Ck3SaveSelection> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Ck3SaveSelection(path, Stamp, new(null, null), Binding));

        public Task<Ck3SaveSelection> BindAsync(Ck3SaveSelection save, Guid snapshotId, CancellationToken cancellationToken = default)
        {
            Binding = new(snapshotId, Stamp);
            return Task.FromResult(save with { Binding = Binding });
        }
    }

    private sealed class SnapshotStore : IPlaysetSnapshotStore
    {
        private ModProfile? _captured;
        private PlaysetSnapshot? _summary;
        public bool ShowDifference { get; init; }
        public Task<IReadOnlyList<PlaysetSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaysetSnapshot>>(_summary is null ? [] : [_summary]);

        public Task<PlaysetSnapshot> CaptureAsync(GameEnvironment environment, ModProfile profile,
            IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default)
        {
            _captured = profile with { Id = Guid.NewGuid(), Name = profile.Name + " · 快照" };
            _summary = new(_captured.Id, profile.Name, environment.GameVersion!, DateTimeOffset.UtcNow,
                profile.Mods.Count(mod => mod.Enabled));
            return Task.FromResult(_summary);
        }

        public Task<ModProfile> RestoreAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_captured!);

        public Task<PlaysetSnapshotComparison> CompareAsync(GameEnvironment environment, Guid id, ModProfile profile,
            IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaysetSnapshotComparison(environment.GameVersion!, ShowDifference ? ["Mod 文件不同"] : []));

        public Task ValidateGameVersionAsync(GameEnvironment environment, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Guid? DeletedId { get; private set; }
        public Task DeleteAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default)
        { DeletedId = id; _summary = null; _captured = null; return Task.CompletedTask; }
    }

    private sealed class Source : IGameLocator, IModScanner, IProfileStore, ISettingsStore, IWorkshopSource, IExternalNavigator, IGameModActivator
    {
        public bool FailApply { get; set; }
        public bool FailProfileSave { get; set; }
        public bool ReverseAppliedOrder { get; init; }
        public bool MissingB { get; set; }
        public bool ExternalChanged { get; set; }
        public bool WaitForReadCancellation { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ApplyCalls { get; private set; }
        public int LaunchCalls { get; private set; }
        public ModProfile? Saved { get; private set; }
        public ModProfile? Applied { get; private set; }
        private bool _nativeWorkshop;
        private static string WorkshopRoot => Path.GetFullPath("test/workshop");
        private InstalledMod[] Mods => [new("a", "战役界面增强", "test/a", "test/a.mod", "1", "1.20.*", null, [], ModOrigin.Local, true),
            new("b", "历史纹章补充", _nativeWorkshop ? Path.Combine(WorkshopRoot, "123") : "test/b", "test/b.mod", "1", "1.20.*",
                _nativeWorkshop ? 123UL : null, [], _nativeWorkshop ? ModOrigin.SteamWorkshop : ModOrigin.Local, !MissingB)
                { Management = _nativeWorkshop ? ModInstallManagement.Steam : ModInstallManagement.Local }];
        public MainWindowViewModel Create(IPlaysetSnapshotStore? snapshots = null, ICk3SaveBindingStore? saves = null,
            IWorkshopStatusSource? updates = null)
        {
            _nativeWorkshop = updates is not null;
            return new(new WorkspaceService(this, this, this, this, this, snapshots, saves, updates), new WorkshopService(this), this);
        }
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment("test/game", "test/user", "1.20.1", _nativeWorkshop ? [WorkshopRoot] : [], []));
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) => Task.FromResult(new ModScanResult(Mods, []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>(Saved is null ? [] : [Saved]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
        {
            if (FailProfileSave) throw new IOException("保存失败");
            Saved = profile;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { if (Saved?.Id == id) Saved = null; return Task.CompletedTask; }
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void LaunchGame() { LaunchCalls++; }
        public void OpenWorkshop(ulong? publishedFileId = null) { }
        public void OpenDirectory(string path) { }
        public async Task<ModProfile?> ReadAsync(GameEnvironment environment, IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default)
        {
            if (WaitForReadCancellation)
            {
                ReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return ExternalChanged
            ? new ModProfile(Guid.NewGuid(), "外部选择", "1.20.1", Mods.Select((mod, i) => new ProfileMod(mod.Id, mod.Name, mod.ContentPath, null, mod.SupportedGameVersion) { Enabled = i == 1 }).ToArray(), DateTimeOffset.UtcNow)
            : Applied is { } applied
                ? ReverseAppliedOrder ? applied with { Mods = applied.Mods.Reverse().ToArray() } : applied
                : new ModProfile(Guid.NewGuid(), "当前游戏配置", "1.20.1", Mods.Select((mod, i) => new ProfileMod(mod.Id, mod.Name, mod.ContentPath, null, mod.SupportedGameVersion) { Enabled = i == 0 }).ToArray(), DateTimeOffset.UtcNow);
        }
        public Task<GameProfileApplyResult> ApplyAsync(GameEnvironment environment, ModProfile profile, IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (FailApply) throw new InvalidOperationException("启动器正在运行");
            ExternalChanged = false;
            Applied = profile;
            return Task.FromResult(new GameProfileApplyResult(profile.Name, profile.Mods.Count(mod => mod.Enabled), "test/backup.sqlite"));
        }
    }
}
