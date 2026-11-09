using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class StrictWorkshopFilteringTests
{
    [Fact]
    public async Task CompatibleGroupsAppearFirstWhileOtherReleasesRemainVisible()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();

        var row = Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched));
        Assert.Equal("Pack", row.Title);
        Assert.Equal(202UL, row.Item.PublishedFileId);
        Assert.Equal(2, vm.WorkshopResults.Count);
        Assert.Equal(row, vm.WorkshopResults[0]);
        Assert.Equal(3, row.Versions.Count);
        Assert.Contains(row.Versions, option => option.Choice.Compatibility == CompatibilityStatus.Unknown);
        Assert.Contains(row.Versions, option => option.Choice.Compatibility == CompatibilityStatus.DeclaredMismatch);
        Assert.Equal("兼容", row.Recommendation);
        Assert.Equal("不兼容", vm.WorkshopResults[1].Recommendation);
        Assert.Equal("当前 CK3 1.20.3", vm.WorkshopRecommendationBasis);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("版本未知")]
    [InlineData("1")]
    public async Task UnknownCurrentVersionBlocksBrowseAndDirectLookupUntilManuallyConfirmed(string? installed)
    {
        var fixture = new Fixture(installed: installed);
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        await vm.LoadWorkshopDetailAsync("202");

        Assert.Empty(fixture.BrowseRequests);
        Assert.Empty(fixture.DetailRequests);
        Assert.Empty(vm.WorkshopResults);
        Assert.Empty(vm.DetailVersions);
        Assert.False(vm.HasWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.False(vm.LookupWorkshopCommand.CanExecute(null));
        Assert.True(vm.HasError);
        Assert.Contains("检查游戏路径", vm.ErrorMessage);
        Assert.Contains("填写", vm.WorkshopRecommendationBasis);

        vm.WorkshopTargetVersion = "1.20.4";
        await vm.SearchWorkshopAsync();
        Assert.Single(fixture.BrowseRequests);
        Assert.Equal(202UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task TargetChangesRerankGroupsWithoutAnotherSteamRequest()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Equal(202UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);

        vm.WorkshopTargetVersion = "1.21.1";
        Assert.Equal("Another", Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Title);
        Assert.Equal(404UL, vm.WorkshopResults[0].Item.PublishedFileId);
        vm.WorkshopTargetVersion = "";
        Assert.Empty(vm.WorkshopResults);
        Assert.True(vm.WorkshopNotLoaded);
        Assert.False(vm.NoWorkshopResults);
        vm.WorkshopTargetVersion = "1.19.2";
        var pack = vm.WorkshopResults.Single(row => row.Title == "Pack");
        Assert.Equal(101UL, pack.Versions[0].Choice.Item.PublishedFileId);
        pack.SelectedRelease = pack.Versions[0];
        Assert.Equal("兼容", pack.Recommendation);
        Assert.Single(fixture.BrowseRequests);
    }

    [Theory]
    [InlineData(101UL)]
    [InlineData(303UL)]
    public async Task DirectIdWithMismatchingOrUnknownDeclarationClearsPreviousDetailsAndCannotSubscribe(ulong id)
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.LoadWorkshopDetailAsync("202");
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));

        vm.WorkshopInput = $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
        await vm.LookupWorkshopAsync();

        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.Null(vm.SelectedDetailVersion);
        Assert.False(vm.CanDownloadWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        vm.DownloadWorkshopCommand.Execute(null);
        Assert.Empty(fixture.OpenedIds);
        Assert.Contains("不兼容或兼容性未知", vm.ErrorMessage);
        Assert.DoesNotContain("非实测", vm.ErrorMessage);
    }

    [Fact]
    public async Task ChangingTargetClosesNowInvalidDetailsAndClearsSubscriptionState()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        vm.WorkshopResults[0].OpenCommand.Execute(null);
        Assert.True(vm.HasWorkshopItem);

        vm.WorkshopTargetVersion = "1.21.1";

        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.Null(vm.SelectedDetailVersion);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.Equal(404UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
        Assert.Single(fixture.DetailRequests);
    }

    [Fact]
    public async Task MismatchingPageRemainsVisibleAndKeepsSteamPagination()
    {
        var fixture = new Fixture();
        fixture.GetPage = request => new WorkshopBrowsePage(
            request.Page == 1 ? [fixture.Items[0]] : [fixture.Items[1]], request.Page, 3, 42, [], ["1.20", "1.19"]);
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.False(vm.NoWorkshopResults);
        Assert.Equal("不兼容", Assert.Single(vm.WorkshopResults).Recommendation);
        Assert.Contains("第 1 页", vm.Status);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.Contains("Steam 共 42 条发布", vm.WorkshopResultLabel);
        Assert.True(vm.NextWorkshopPageCommand.CanExecute(null));

        vm.NextWorkshopPageCommand.Execute(null);
        Assert.Equal(new[] { 1, 2 }, fixture.BrowseRequests.Select(request => request.Page));
        Assert.Equal(202UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
        Assert.Equal("第 2 / 3 页", vm.WorkshopPageLabel);
    }

    [Fact]
    public async Task TopVersionFiltersOnlyContainMatchingSteamTagsAndResetWhenTargetChanges()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Equal("全部版本", vm.WorkshopVersions[0].Label);
        Assert.Equal(new string?[] { null, "1.20 'Crozier'" }, vm.WorkshopVersions.Select(filter => filter.Value));
        vm.SelectedWorkshopVersion = vm.WorkshopVersions[1];
        await vm.SearchWorkshopAsync();
        Assert.Equal("1.20 'Crozier'", fixture.BrowseRequests[^1].VersionTag);

        vm.WorkshopTargetVersion = "1.19.2";
        Assert.Null(vm.SelectedWorkshopVersion?.Value);
        Assert.Equal(new string?[] { null, "1.19 'Basileus'" }, vm.WorkshopVersions.Select(filter => filter.Value));
        vm.SelectedWorkshopVersion = new WorkshopFilter("错误版本", "1.21");
        Assert.Null(vm.SelectedWorkshopVersion?.Value);
        Assert.False(vm.NextWorkshopPageCommand.CanExecute(null));
        Assert.False(vm.PreviousWorkshopPageCommand.CanExecute(null));
        Assert.Contains("重新搜索", vm.Status);
        Assert.Contains("原筛选结果", vm.WorkshopResultLabel);
        var requestsBeforeCachedSearch = fixture.BrowseRequests.Count;
        await vm.SearchWorkshopAsync();
        Assert.Equal(requestsBeforeCachedSearch, fixture.BrowseRequests.Count);
        Assert.Null(vm.SelectedWorkshopVersion?.Value);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.DoesNotContain("原筛选结果", vm.WorkshopResultLabel);
        await vm.SearchWorkshopAsync(useActiveQuery: true, forceRefresh: true);
        Assert.Equal(requestsBeforeCachedSearch + 1, fixture.BrowseRequests.Count);
        Assert.Equal(1, fixture.BrowseRequests[^1].Page);
        Assert.Null(fixture.BrowseRequests[^1].VersionTag);
    }

    [Fact]
    public async Task RevalidatedMetadataUpdatesCompatibilityBeforeOpeningDetails()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        fixture.DetailItems[202] = fixture.Items[1] with { Tags = ["1.18"] };

        vm.WorkshopResults[0].OpenCommand.Execute(null);

        Assert.False(vm.HasWorkshopItem);
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.Empty(vm.DetailVersions);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task AStillMatchingManualSelectionSurvivesTargetChangesAndRefreshes()
    {
        var items = new[] { Item(1, "Pack", "1.20", 1), Item(2, "Pack", "1.20", 2) };
        var fixture = new Fixture(items);
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched));
        row.SelectedRelease = row.Versions.Single(option => option.Choice.Item.PublishedFileId == 1);

        vm.WorkshopTargetVersion = "1.20.5";
        Assert.Same(row, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)));
        Assert.Equal(1UL, row.Item.PublishedFileId);
        await vm.RefreshAsync();
        Assert.Equal(1UL, row.Item.PublishedFileId);
        await vm.SearchWorkshopAsync();
        Assert.Equal(1UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
    }

    [Fact]
    public async Task LocalVersionRefreshRebuildsAllCachedGroupsAndInvalidatesOldDetails()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        await vm.LoadWorkshopDetailAsync("202");
        fixture.Version = "1.21.2";

        await vm.RefreshAsync();

        Assert.Equal("1.21.2", vm.WorkshopTargetVersion);
        Assert.Equal(404UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
        Assert.False(vm.HasWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.Single(fixture.BrowseRequests);

    }

    [Fact]
    public async Task RejectedDirectLookupOverridesAnOlderBrowseEntryEvenBeforeTheFirstSearch()
    {
        var item = Item(202, "Pack", "1.20");
        var fixture = new Fixture([item]);
        fixture.DetailItems[202] = item with { Tags = ["1.19"] };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);

        await vm.LoadWorkshopDetailAsync("202");
        Assert.True(vm.HasError);
        Assert.Empty(fixture.BrowseRequests);
        await vm.SearchWorkshopAsync();

        Assert.Single(fixture.BrowseRequests);
        Assert.Single(fixture.DetailRequests);
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenedDetailsKeepTheirCompatibilityDeclarationAfterFreshnessExpiresUntilANewerPublicationArrives()
    {
        var clock = new ManualClock();
        var item = Item(202, "Pack", "1.20");
        var fixture = new Fixture([item]) { Clock = clock };
        fixture.DetailItems[202] = item with { Tags = ["1.20.3"] };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.LoadWorkshopDetailAsync("202");
        vm.WorkshopTargetVersion = "1.20.4";

        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromMilliseconds(1));
        await vm.SearchWorkshopAsync();
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.SearchWorkshopAsync();
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.Single(fixture.DetailRequests);

        // 详情过了新鲜期，列表仍使用已核对的同修订兼容声明。
        await vm.SearchWorkshopAsync(forceRefresh: true);
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.False(vm.CanDownloadWorkshopItem);

        fixture.GetPage = request => new WorkshopBrowsePage(
            [item with { UpdatedAt = item.UpdatedAt.AddSeconds(1) }], request.Page, 1, 1, [], ["1.20"]);
        await vm.SearchWorkshopAsync(forceRefresh: true);
        Assert.Equal(202UL, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.PublishedFileId);
        Assert.False(vm.HasWorkshopItem);
        Assert.False(vm.CanDownloadWorkshopItem);
    }

    [Fact]
    public async Task ANewerBrowsePublicationCanSupersedeTheVerifiedDetailOverlay()
    {
        var item = Item(202, "Pack", "1.20");
        var fixture = new Fixture([item]);
        fixture.DetailItems[202] = item with { Tags = ["1.20.3"] };
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.LoadWorkshopDetailAsync("202");
        vm.WorkshopTargetVersion = "1.20.4";
        var newPublication = item with { Tags = ["1.20.4"], UpdatedAt = item.UpdatedAt.AddSeconds(1) };
        fixture.GetPage = request => new WorkshopBrowsePage([newPublication], request.Page, 1, 1, [], ["1.20.4"]);

        await vm.SearchWorkshopAsync();

        Assert.Equal(new[] { "1.20.4" }, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)).Item.Tags);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task SuccessfulDetailNarrowingTheDeclarationUpdatesCachedRowsBeforeTargetChanges()
    {
        var item = Item(202, "Pack", "1.20");
        var fixture = new Fixture([item]);
        using var vm = fixture.Create();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched));
        fixture.DetailItems[202] = item with { Tags = ["1.20.3"] };

        row.OpenCommand.Execute(null);

        Assert.True(vm.HasWorkshopItem);
        Assert.Same(row, Assert.Single(vm.WorkshopResults.Where(row => row.IsCompatibilityMatched)));
        Assert.Equal(new[] { "1.20.3" }, row.Item.Tags);
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));
        vm.WorkshopTargetVersion = "1.20.4";
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.Single(fixture.BrowseRequests);

        await vm.SearchWorkshopAsync();
        Assert.Single(fixture.BrowseRequests);
        Assert.Single(fixture.DetailRequests);
        Assert.DoesNotContain(vm.WorkshopResults, row => row.IsCompatibilityMatched);
        Assert.False(vm.HasWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("1.02")]
    [InlineData(null)]
    public async Task PrisonArchitectBrowsesUndeclaredItemsAndAllowsDownloadWhenVersionKnown(string? installed)
    {
        var item = Item(303, "Prison Architect test mod", null) with { ConsumerAppId = GameCatalog.PrisonArchitect.AppId };
        var fixture = new Fixture([item], installed);
        using var vm = fixture.Create(GameCatalog.PrisonArchitect);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        await vm.SearchWorkshopAsync();
        Assert.Single(vm.WorkshopResults);

        await vm.LoadWorkshopDetailAsync("303");

        Assert.True(vm.HasWorkshopItem);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Equal(CompatibilityStatus.Unknown, vm.SelectedDetailVersion!.Choice.Compatibility);
        Assert.Equal(installed is not null, vm.CanDownloadWorkshopItem);
        Assert.Empty(fixture.OpenedIds);
        vm.OpenWorkshopItemCommand.Execute(null);
        Assert.Equal(303UL, Assert.Single(fixture.OpenedIds));
    }

    private static WorkshopItem Item(ulong id, string name, string? version, int day = 1) => new(
        id, Ck3.AppId, name, "说明", null, new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"),
        DateTimeOffset.UnixEpoch.AddDays(day), 1, version is null ? ["Events"] : [version], null, CreatorId: "author");

    private sealed class Fixture : IGameLocator, IModScanner, IProfileStore, ISettingsStore, IWorkshopSource, IWorkshopBrowser, IExternalNavigator
    {
        public Fixture(IReadOnlyList<WorkshopItem>? items = null, string? installed = "1.20.3")
        {
            Items = items ?? [Item(101, "Pack", "1.19"), Item(202, "Pack", "1.20"), Item(303, "Pack", null), Item(404, "Another", "1.21")];
            Version = installed;
            DetailItems = Items.ToDictionary(item => item.PublishedFileId);
        }

        public IReadOnlyList<WorkshopItem> Items { get; }
        public string? Version { get; set; }
        public Dictionary<ulong, WorkshopItem> DetailItems { get; }
        public List<WorkshopSearchRequest> BrowseRequests { get; } = [];
        public List<ulong> DetailRequests { get; } = [];
        public List<ulong?> OpenedIds { get; } = [];
        public Func<WorkshopSearchRequest, WorkshopBrowsePage>? GetPage { get; set; }
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        public MainWindowViewModel Create(GameDefinition? game = null) => new(new WorkspaceService(this, this, this, this),
            new WorkshopService(this, this, timeProvider: Clock, game: game), this, Clock);
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
        {
            BrowseRequests.Add(request);
            return Task.FromResult(GetPage?.Invoke(request) ?? new WorkshopBrowsePage(Items, request.Page, 3, 42,
                [], ["1.21", "1.20 'Crozier'", "1.19 'Basileus'", "Graphics"]));
        }
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            DetailRequests.Add(publishedFileId);
            return Task.FromResult(DetailItems[publishedFileId]);
        }
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, "fixture-data", Version, [], []));
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModScanResult([], []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LaunchGame() => throw new InvalidOperationException("筛选测试不应启动游戏。");
        public void OpenWorkshop(ulong? publishedFileId = null) => OpenedIds.Add(publishedFileId);
        public void OpenDirectory(string path) => throw new InvalidOperationException("此测试不应访问用户目录。");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
