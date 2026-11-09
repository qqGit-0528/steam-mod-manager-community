using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class VersionSelectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorReleasesBecomeOneRowAndDefaultToInstalledVersionMatch(bool explicitTitleVersions)
    {
        var fixture = new Fixture { Items = Releases(explicitTitleVersions) };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();

        var row = Assert.Single(vm.WorkshopResults);
        Assert.Equal("Campaign", row.Title);
        Assert.Equal(3, row.Versions.Count);
        Assert.Equal("1.20.3", vm.WorkshopTargetVersion);
        Assert.Equal(202UL, row.Item.PublishedFileId);
        Assert.Equal(CompatibilityStatus.DeclaredMatch, row.SelectedRelease!.Choice.Compatibility);
        Assert.True(row.SelectedRelease.IsRecommended);
        Assert.Equal(303UL, fixture.Items.OrderByDescending(item => item.UpdatedAt).First().PublishedFileId);
        Assert.Equal("兼容", row.Recommendation);
    }

    [Fact]
    public async Task ChangingRowReleaseUpdatesBoundMetadataAndActualItem()
    {
        var fixture = new Fixture { Items = CompatibleReleases() };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        var originalMetadata = row.Metadata;
        var changedProperties = new List<string?>();
        row.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        row.SelectedRelease = row.Versions.Single(version => version.Choice.Item.PublishedFileId == 101);
        await row.PreviewLoadingTask;

        Assert.Equal(101UL, row.Item.PublishedFileId);
        Assert.Equal("旧版本说明", row.Summary);
        Assert.NotEqual(originalMetadata, row.Metadata);
        Assert.Contains("11 人订阅", row.Metadata);
        Assert.Contains("1.20", row.Tags);
        Assert.Contains(nameof(row.Item), changedProperties);
        Assert.Contains(nameof(row.Summary), changedProperties);
        Assert.Contains(nameof(row.Metadata), changedProperties);
        Assert.Empty(fixture.DetailRequests);
    }

    [Fact]
    public async Task OpeningSelectedReleaseAndSwitchingDetailVersionUsesCorrectWorkshopIdentity()
    {
        var fixture = new Fixture { Items = CompatibleReleases() };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.SelectedRelease = row.Versions.Single(version => version.Choice.Item.PublishedFileId == 101);

        // 此 fixture 的查询立即完成，便于直接验证绑定命令触发的真实条目选择。
        row.OpenCommand.Execute(null);
        await vm.WorkshopLoadingTask;
        await vm.PreviewLoadingTask;
        Assert.Equal(101UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.Equal(3, vm.DetailVersions.Count);
        Assert.Equal(new[] { 101UL }, fixture.DetailRequests);

        vm.SelectedDetailVersion = vm.DetailVersions.Single(version => version.Choice.Item.PublishedFileId == 303);
        await vm.WorkshopLoadingTask;
        await vm.PreviewLoadingTask;
        Assert.Equal(303UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(303UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.Equal("新版说明", vm.WorkshopDescription);
        Assert.Equal(new[] { 101UL, 303UL }, fixture.DetailRequests);
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));
        vm.DownloadWorkshopCommand.Execute(null);
        Assert.Empty(fixture.Navigator.Opened);

        await vm.RefreshAsync();
        Assert.Equal(303UL, row.Item.PublishedFileId);
        Assert.Equal(303UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);

        vm.BackToWorkshopCommand.Execute(null);
        Assert.False(vm.HasWorkshopItem);
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(303UL, row.Item.PublishedFileId);
        Assert.Equal(303UL, row.SelectedRelease!.Choice.Item.PublishedFileId);
        row.OpenCommand.Execute(null);
        await vm.WorkshopLoadingTask;
        await vm.PreviewLoadingTask;
        Assert.Equal(new[] { 101UL, 303UL }, fixture.DetailRequests);
        Assert.Equal(303UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(303UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.Equal("新版说明", vm.WorkshopDescription);
        Assert.True(vm.CanDownloadWorkshopItem);
        vm.DownloadWorkshopCommand.Execute(null);
        Assert.Empty(fixture.Navigator.Opened);
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task RefreshKeepsManuallySelectedReleaseWhenGameVersionHasNotChanged()
    {
        var fixture = new Fixture { Items = CompatibleReleases() };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.SelectedRelease = row.Versions.Single(version => version.Choice.Item.PublishedFileId == 101);
        await vm.RefreshAsync();
        Assert.Equal(101UL, row.Item.PublishedFileId);
    }

    [Fact]
    public async Task ChangingTargetReranksChoicesWithoutDiscardingTheSelectedRelease()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);

        vm.WorkshopTargetVersion = "1.19.2";
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Versions[0].Choice.Item.PublishedFileId);
        Assert.Equal(202UL, row.Item.PublishedFileId);
        Assert.Equal("不兼容", row.Recommendation);
        Assert.True(row.Versions[0].IsRecommended);
        Assert.Contains("1.19.2", vm.WorkshopRecommendationBasis);

        vm.UseInstalledVersionCommand.Execute(null);
        Assert.Equal("1.20.3", vm.WorkshopTargetVersion);
        Assert.Equal(202UL, row.Item.PublishedFileId);
        Assert.Equal(202UL, row.Versions[0].Choice.Item.PublishedFileId);
        Assert.True(row.Versions[0].IsRecommended);
        Assert.Contains("1.20.3", vm.WorkshopRecommendationBasis);
    }

    [Fact]
    public async Task ExplicitTargetBeforeSearchOverridesInstalledVersionForTheDefaultSelection()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        vm.WorkshopTargetVersion = "1.21.0";
        await vm.SearchWorkshopAsync();
        Assert.Equal(303UL, Assert.Single(vm.WorkshopResults).Item.PublishedFileId);
    }

    [Fact]
    public async Task UnknownInstalledVersionNeverLabelsTheNewestReleaseAsRecommended()
    {
        var fixture = new Fixture { InstalledVersion = null };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Equal("", vm.WorkshopTargetVersion);
        Assert.Contains("未检测", vm.WorkshopRecommendationBasis);
        Assert.Empty(vm.WorkshopResults);
        Assert.Empty(vm.DetailVersions);
        Assert.True(vm.HasError);
        Assert.Contains("检查游戏路径", vm.ErrorMessage);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
    }

    [Fact]
    public async Task MultipleCompatibilityTagsDoNotInventAdditionalReleasesOrModVersions()
    {
        var item = Releases()[0] with { Title = "Campaign", Tags = ["1.19 'Basileus'", "1.20 'Crozier'"] };
        var fixture = new Fixture { Items = [item] };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        var choice = Assert.Single(row.Versions);
        Assert.Equal(new[] { "1.19", "1.20" }, choice.Choice.GameVersions);
        Assert.Null(choice.Choice.ModVersion);
        Assert.Equal("1 个发布", row.VersionCount);

        row.OpenCommand.Execute(null);
        await vm.PreviewLoadingTask;
        Assert.Single(vm.DetailVersions);
        Assert.Equal("", vm.WorkshopReleaseAvailability);
    }

    [Fact]
    public async Task SteamPublishingCountsRemainDistinctFromCurrentPageGroupCounts()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Single(vm.WorkshopResults);
        Assert.Equal("本页 1 个 Mod · Steam 共 42 条发布", vm.WorkshopResultLabel);
        Assert.Equal("第 1 / 7 页", vm.WorkshopPageLabel);
        Assert.True(vm.NextWorkshopPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task MatchingNamesFromDifferentCreatorsRemainSeparateRows()
    {
        var baseReleases = Releases();
        var fixture = new Fixture
        {
            Items = [.. baseReleases, baseReleases[1] with
            {
                PublishedFileId = 404,
                WorkshopUrl = new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=404"),
                CreatorId = "other-author"
            }]
        };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Equal(2, vm.WorkshopResults.Count);
        Assert.Equal(new[] { 1, 3 }, vm.WorkshopResults.Select(row => row.Versions.Count).Order());
    }

    private static IReadOnlyList<WorkshopItem> CompatibleReleases() => Releases().Select(item => item with
    {
        Title = "Campaign", Tags = ["1.20"]
    }).ToArray();

    private static IReadOnlyList<WorkshopItem> Releases(bool explicitTitleVersions = true) =>
    [
        Item(101, explicitTitleVersions ? "Campaign [CK3 1.19]" : "Campaign", "旧版本说明", "1.19", 20, 11),
        Item(202, explicitTitleVersions ? "Campaign (CK3 1.20)" : "Campaign", "适配本机版本说明", "1.20", 10, 22),
        Item(303, explicitTitleVersions ? "Campaign - CK3 1.21" : "Campaign", "新版说明", "1.21", 30, 33)
    ];

    private static WorkshopItem Item(ulong id, string title, string description, string version, int updated, long subscribers) =>
        new(id, Ck3.AppId, title, description, null, new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"),
            DateTimeOffset.UnixEpoch.AddDays(updated), 100, [version], null, subscribers, "author");

    private sealed class Fixture
    {
        public IReadOnlyList<WorkshopItem> Items { get; init; } = Releases();
        public string? InstalledVersion { get; init; } = "1.20.3";
        public List<ulong> DetailRequests { get; } = [];
        public Navigator Navigator { get; } = new();

        public MainWindowViewModel Create()
        {
            var ports = new MemoryPorts(InstalledVersion);
            return new MainWindowViewModel(new WorkspaceService(ports, ports, ports, ports),
                new WorkshopService(new Source(Items, DetailRequests), new Browser(Items)), Navigator);
        }
    }

    private sealed class Source(IReadOnlyList<WorkshopItem> items, List<ulong> requested) : IWorkshopSource
    {
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requested.Add(publishedFileId);
            return Task.FromResult(items.Single(item => item.PublishedFileId == publishedFileId));
        }
    }

    private sealed class Browser(IReadOnlyList<WorkshopItem> items) : IWorkshopBrowser
    {
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkshopBrowsePage(items, request.Page, 7, 42, [], ["1.21", "1.20", "1.19"]));
    }

    private sealed class MemoryPorts(string? version) : IGameLocator, IModScanner, IProfileStore, ISettingsStore
    {
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, "fixture-data", version, [], []));
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModScanResult([], []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Navigator : IExternalNavigator
    {
        public List<ulong?> Opened { get; } = [];
        public void LaunchGame() => throw new InvalidOperationException("版本选择不应启动游戏。");
        public void OpenWorkshop(ulong? publishedFileId = null) => Opened.Add(publishedFileId);
        public void OpenDirectory(string path) => throw new InvalidOperationException("版本选择不应打开目录。");
    }
}
