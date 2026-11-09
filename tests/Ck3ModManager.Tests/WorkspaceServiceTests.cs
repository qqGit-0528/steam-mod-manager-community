using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkspaceServiceTests
{
    [Fact]
    public async Task InitializeUsesSavedSettingsAndDiscoveryBeforeScan()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new WorkspacePorts();
        var state = await ports.Service.InitializeAsync(cancellation.Token);
        Assert.Same(ports.Settings, state.Settings);
        Assert.Same(ports.Environment, state.Environment);
        Assert.Same(ports.Scan, state.Scan);
        Assert.Same(ports.Profiles, state.Profiles);
        Assert.Equal(new[] { "settings.load", "discover", "scan", "profiles.load" }, ports.Calls);
        Assert.Equal(ports.Settings.ToDiscoveryOptions(), ports.DiscoveryOptions);
        Assert.Equal(ports.Environment.UserDataDirectory, ports.ScannedUserData);
        Assert.Same(ports.Environment.WorkshopDirectories, ports.ScannedWorkshopDirectories);
        Assert.All(ports.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task RefreshUsesProvidedSettingsWithoutSavingOrReloadingSettings()
    {
        var ports = new WorkspacePorts();
        var changed = new AppSettings(GameDirectory: "new-game");
        var state = await ports.Service.RefreshAsync(changed);
        Assert.Same(changed, state.Settings);
        Assert.Equal(changed.ToDiscoveryOptions(), ports.DiscoveryOptions);
        Assert.Equal(new[] { "discover", "scan", "profiles.load" }, ports.Calls);
    }

    [Fact]
    public async Task SaveSettingsDoesNotDiscoverOrScan()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new WorkspacePorts();
        var changed = new AppSettings(UserDataDirectory: "custom-data");
        await ports.Service.SaveSettingsAsync(changed, cancellation.Token);
        Assert.Same(changed, ports.SavedSettings);
        Assert.Equal(new[] { "settings.save" }, ports.Calls);
        Assert.Equal(cancellation.Token, Assert.Single(ports.Tokens));
    }

    [Fact]
    public async Task SaveProfilePreservesUserOrderAndEntireProfile()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new WorkspacePorts();
        var profile = MakeProfile("我的顺序", [MakeMod("z"), MakeMod("a"), MakeMod("patch")]);
        await ports.Service.SaveProfileAsync(profile, cancellation.Token);
        Assert.Same(profile, ports.SavedProfile);
        Assert.Equal(new[] { "z", "a", "patch" }, ports.SavedProfile!.Mods.Select(mod => mod.InstalledModId));
        Assert.Equal(new[] { "profiles.save" }, ports.Calls);
        Assert.Equal(cancellation.Token, Assert.Single(ports.Tokens));
    }

    [Fact]
    public async Task DeleteProfileForwardsIdToStore()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new WorkspacePorts();
        var id = Guid.NewGuid();
        await ports.Service.DeleteProfileAsync(id, cancellation.Token);
        Assert.Equal(id, ports.DeletedProfileId);
        Assert.Equal(new[] { "profiles.delete" }, ports.Calls);
        Assert.Equal(cancellation.Token, Assert.Single(ports.Tokens));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RejectsEmptyProfileNameBeforeSaving(string name)
    {
        var ports = new WorkspacePorts();
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service.SaveProfileAsync(MakeProfile(name, [])));
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task RejectsDuplicateModIdsBeforeSaving()
    {
        var ports = new WorkspacePorts();
        var profile = MakeProfile("重复", [MakeMod("same"), MakeMod("same")]);
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service.SaveProfileAsync(profile));
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task RejectsMissingModIdBeforeSaving()
    {
        var ports = new WorkspacePorts();
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service.SaveProfileAsync(MakeProfile("名称", [MakeMod(" ")])));
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task PreCanceledInitializeDoesNotReadAnyPort()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ports = new WorkspacePorts();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service.InitializeAsync(cancellation.Token));
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task CancellationAfterDiscoveryPreventsScanAndProfileLoad()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new WorkspacePorts { AfterDiscover = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service.RefreshAsync(ports.Settings, cancellation.Token));
        Assert.Equal(new[] { "discover" }, ports.Calls);
    }

    [Fact]
    public async Task CanceledSavesDoNotWriteStores()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ports = new WorkspacePorts();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service.SaveSettingsAsync(ports.Settings, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service.SaveProfileAsync(MakeProfile("配置", []), cancellation.Token));
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task BatchCheckIncludesOnlyEnabledInstalledNativeWorkshopItemsAndDeduplicatesTheirIds()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Path.GetFullPath("workshop/content/1158310");
        var extra = Path.GetFullPath("second/workshop/content/1158310");
        InstalledMod Mod(string id, string content, ulong? item, ModOrigin origin = ModOrigin.SteamWorkshop, bool available = true,
            ModInstallManagement management = ModInstallManagement.Steam) =>
            new(id, id, content, content, null, null, item, [], origin, available) { Management = management };
        var scan = new ModScanResult([
            Mod("a", Path.Combine(folder, "101"), 101),
            Mod("duplicate", Path.Combine(extra, "101"), 101),
            Mod("b", Path.Combine(folder, "202"), 202),
            Mod("disabled", Path.Combine(folder, "303"), 303),
            Mod("missing", Path.Combine(folder, "404"), 404, available: false),
            Mod("different-item-folder", Path.Combine(folder, "666"), 606, management: ModInstallManagement.External),
            Mod("snapshot", Path.GetFullPath("user/mod/app_mm_snapshot_test"), null, ModOrigin.Local, management: ModInstallManagement.Local),
            Mod("local-with-old-id", Path.GetFullPath("user/mod/505"), 505, management: ModInstallManagement.Local),
            Mod("application-copy", Path.GetFullPath("content/steam/707"), 707, management: ModInstallManagement.Application)
        ], []);
        var ports = new WorkspacePorts { Scan = scan, Environment = new("game", "data", "1.12", [folder, extra], []) };
        var states = new WorkshopStatusStub();
        var service = new WorkspaceService(ports, ports, ports, ports, workshopStatus: states);
        var profile = MakeProfile("当前战役", scan.Mods.Select(mod => MakeMod(mod.Id) with { Enabled = mod.Id != "disabled" }).ToArray());
        var result = await service.CheckWorkshopUpdatesAsync(ports.Settings, profile, token);

        Assert.Equal(new ulong[] { 101, 202 }, states.Items);
        Assert.Equal(new[] { WorkshopUpdateState.NeedsUpdate, WorkshopUpdateState.Unknown }, result.Entries.Select(row => row.State));
        Assert.Equal(new[] { "discover", "scan" }, ports.Calls);
    }

    private sealed class WorkshopStatusStub : IWorkshopStatusSource
    {
        public IReadOnlyList<ulong> Items { get; private set; } = [];

        public Task<WorkshopStatusCheck> CheckAsync(GameEnvironment environment, IReadOnlyList<ulong> itemIds,
            CancellationToken cancellationToken = default)
        {
            Items = itemIds;
            return Task.FromResult(new WorkshopStatusCheck(new Dictionary<ulong, WorkshopUpdateState> { [101] = WorkshopUpdateState.NeedsUpdate }));
        }
    }

    private static ProfileMod MakeMod(string id) => new(id, id, $"mods/{id}", null, "1.12.*");

    [Fact]
    public async Task RejectsTwoVersionsOfSameWorkshopProjectInOneProfile()
    {
        var ports = new WorkspacePorts();
        var profile = MakeProfile("不能同时加载", [MakeMod("copy-a") with { WorkshopId = 123 }, MakeMod("copy-b") with { WorkshopId = 123 }]);
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service.SaveProfileAsync(profile, TestContext.Current.CancellationToken));
        Assert.Empty(ports.Calls);
    }
    private static ModProfile MakeProfile(string name, IReadOnlyList<ProfileMod> mods) =>
        new(Guid.NewGuid(), name, "1.12.5", mods, DateTimeOffset.UnixEpoch);

    private sealed class WorkspacePorts : IGameLocator, IModScanner, IProfileStore, ISettingsStore
    {
        public AppSettings Settings { get; } = new("steam", "game", "data");
        public GameEnvironment Environment { get; init; } = new("game", "discovered-data", "1.12.5", ["workshop"], []);
        public ModScanResult Scan { get; init; } = new([], ["示例警告"]);
        public IReadOnlyList<ModProfile> Profiles { get; } = [MakeProfile("原配置", [MakeMod("z"), MakeMod("a")])];
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public DiscoveryOptions? DiscoveryOptions { get; private set; }
        public string? ScannedUserData { get; private set; }
        public IReadOnlyList<string>? ScannedWorkshopDirectories { get; private set; }
        public ModProfile? SavedProfile { get; private set; }
        public Guid? DeletedProfileId { get; private set; }
        public AppSettings? SavedSettings { get; private set; }
        public Action? AfterDiscover { get; init; }
        public WorkspaceService Service => new(this, this, this, this);

        private void Record(string operation, CancellationToken token)
        {
            Calls.Add(operation);
            Tokens.Add(token);
        }

        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default)
        {
            Record("discover", cancellationToken);
            DiscoveryOptions = options;
            AfterDiscover?.Invoke();
            return Task.FromResult(Environment);
        }

        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
            CancellationToken cancellationToken = default)
        {
            Record("scan", cancellationToken);
            ScannedUserData = userDataDirectory;
            ScannedWorkshopDirectories = workshopDirectories;
            return Task.FromResult(Scan);
        }

        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken)
        {
            Record("profiles.load", cancellationToken);
            return Task.FromResult(Profiles);
        }

        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
        {
            Record("profiles.save", cancellationToken);
            SavedProfile = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Record("profiles.delete", cancellationToken);
            DeletedProfileId = id;
            return Task.CompletedTask;
        }

        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken)
        {
            Record("settings.load", cancellationToken);
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Record("settings.save", cancellationToken);
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }
}
