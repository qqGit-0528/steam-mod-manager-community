using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class WorkshopViewModelTests
{
    [Fact]
    public async Task TypedFiltersAreSubmittedAndResetClearsBothInputs()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.SearchWorkshopAsync();
        vm.WorkshopCategoryInput = " graphics ";
        vm.WorkshopVersionInput = " 1.17 ";
        await vm.SearchWorkshopAsync();
        Assert.Equal("Graphics", fixture.Browser.Requests[^1].CategoryTag);
        Assert.Equal("1.17", fixture.Browser.Requests[^1].VersionTag);

        vm.WorkshopCategoryInput = "Custom type";
        vm.WorkshopVersionInput = "1.17.1";
        await vm.SearchWorkshopAsync();
        Assert.Equal("Custom type", fixture.Browser.Requests[^1].CategoryTag);
        Assert.Equal("1.17.1", fixture.Browser.Requests[^1].VersionTag);
        await vm.ResetWorkshopAsync();
        Assert.Equal("全部类型", vm.WorkshopCategoryInput);
        Assert.Equal("全部版本", vm.WorkshopVersionInput);
        Assert.Null(vm.SelectedWorkshopCategory!.Value);
        Assert.Null(vm.SelectedWorkshopVersion!.Value);
    }

    [Fact]
    public async Task InvalidTypedVersionDoesNotSilentlyRunAnUnfilteredSearch()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopVersionInput = "1.16";
        await vm.SearchWorkshopAsync();
        Assert.Empty(fixture.Browser.Requests);
        Assert.True(vm.HasError);
        Assert.Contains("版本筛选", vm.ErrorMessage);
        Assert.Equal("1.16", vm.WorkshopVersionInput);
    }

    [AvaloniaFact]
    public async Task FilterArrowShowsAllChoicesAndSelectionAndTypingReachTheSearch()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.SearchWorkshopAsync();
        vm.Section = "workshop";
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var category = window.FindControl<EditableFilterBox>("WorkshopCategoryFilter")!;
            var version = window.FindControl<EditableFilterBox>("WorkshopVersionFilter")!;
            Assert.NotNull(category); Assert.NotNull(version);
            category.Text = "Events"; Dispatcher.UIThread.RunJobs();
            var arrow = (Button)category.InnerRightContent!;
            var arrowPoint = arrow.TranslatePoint(new Point(arrow.Bounds.Width / 2, arrow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(arrowPoint, MouseButton.Left);
            window.MouseUp(arrowPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(category.IsDropDownOpen);
            var textBox = category.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal(textBox.SelectionStart, textBox.SelectionEnd);
            var inputBorder = textBox.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_BorderElement");
            Assert.Equal(new Thickness(1), inputBorder.BorderThickness);
            var popup = category.GetVisualDescendants().OfType<Popup>().Single();
            var list = popup.Child!.GetVisualDescendants().OfType<SelectingItemsControl>().Single();
            Assert.Equal(vm.WorkshopCategories.Count, list.Items.Count);
            list.SelectedItem = vm.WorkshopCategories.Single(option => option.Value == "Graphics");
            category.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Graphics", vm.WorkshopCategoryInput);
            Assert.False(category.IsDropDownOpen);

            version.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Dispatcher.UIThread.RunJobs();
            Assert.True(version.IsDropDownOpen);
            var versionPopup = version.GetVisualDescendants().OfType<Popup>().Single();
            var versionList = versionPopup.Child!.GetVisualDescendants().OfType<SelectingItemsControl>().Single();
            Assert.Equal(vm.WorkshopVersions.Count, versionList.Items.Count);
            version.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(version.IsDropDownOpen);

            textBox.Text = "Custom type"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Custom type", vm.WorkshopCategoryInput);
            version.Text = "1.17"; Dispatcher.UIThread.RunJobs();
            await vm.SearchWorkshopAsync();
            Assert.Equal("Custom type", fixture.Browser.Requests[^1].CategoryTag);
            Assert.Equal("1.17", fixture.Browser.Requests[^1].VersionTag);
            if (Environment.GetEnvironmentVariable("CK3_FILTER_SCREENSHOT_DIRECTORY") is { Length: > 0 } screenshots)
            {
                Directory.CreateDirectory(screenshots);
                GameTheme.Apply(vm.Game);
                await vm.ResetWorkshopAsync();
                window.FindControl<TextBox>("WorkshopReferenceInput")!.Focus();
                window.Width = 1000; window.Height = 690; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "editable-filters.png"), PngBitmapEncoderOptions.Default);
                category.ToggleOptions(); Dispatcher.UIThread.RunJobs();
                list.SelectedItem = vm.WorkshopCategories.Single(option => option.Value == "Graphics");
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "filter-choices.png"), PngBitmapEncoderOptions.Default);
                category.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                version.ToggleOptions(); Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "version-choices.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task NavigatingToWorkshopDuringInitializationLoadsFirstPageAfterDiscoveryCompletes()
    {
        var discoveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture { LocalDiscoveryGate = discoveryGate };
        using var vm = fixture.Create();

        var initialization = vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsBusy);
        Assert.False(initialization.IsCompleted);
        vm.Section = "workshop";
        Assert.True(vm.IsWorkshop);
        Assert.Empty(fixture.Browser.Requests);

        discoveryGate.SetResult();
        await initialization;
        await fixture.Browser.FirstSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(new WorkshopSearchRequest(), Assert.Single(fixture.Browser.Requests));
        Assert.Equal(new[] { 101UL, 202UL }, vm.WorkshopResults.Select(row => row.Item.PublishedFileId));
        Assert.True(vm.HasWorkshopResults);
        Assert.False(vm.WorkshopNotLoaded);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task SearchLoadsBrowserItemsAndAvailableFiltersWithoutLookingUpAnId()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "  汉化  ";
        vm.SelectedWorkshopSort = vm.WorkshopSorts.Single(option => option.Value == WorkshopSort.MostSubscribed);
        vm.SelectedWorkshopCategory = new WorkshopFilter("事件", "Events");
        vm.SelectedWorkshopVersion = new WorkshopFilter("1.17", "1.17");

        await vm.SearchWorkshopAsync();
        await vm.PreviewLoadingTask;

        Assert.Equal(new WorkshopSearchRequest("汉化", WorkshopSort.MostSubscribed, 1, "Events", "1.17"), Assert.Single(fixture.Browser.Requests));
        Assert.Equal(new[] { 101UL, 202UL }, vm.WorkshopResults.Select(row => row.Item.PublishedFileId));
        Assert.Equal("地图扩展", vm.WorkshopResults[0].Title);
        Assert.Equal("新增地图 内容", vm.WorkshopResults[0].Summary);
        Assert.All(vm.WorkshopResults, row => Assert.False(row.HasPreview));
        Assert.Contains(vm.WorkshopCategories, filter => filter.Value == "Graphics");
        Assert.Contains(vm.WorkshopVersions, filter => filter.Value == "1.17");
        Assert.Equal("Events", vm.SelectedWorkshopCategory?.Value);
        Assert.Equal("1.17", vm.SelectedWorkshopVersion?.Value);
        Assert.True(vm.HasWorkshopResults);
        Assert.False(vm.NoWorkshopResults);
        Assert.False(vm.WorkshopNotLoaded);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.Contains("5", vm.WorkshopResultLabel);
        Assert.True(vm.NextWorkshopPageCommand.CanExecute(null));
        Assert.False(vm.PreviousWorkshopPageCommand.CanExecute(null));
        Assert.Empty(fixture.Source.RequestedIds);
        Assert.Empty(fixture.Navigator.OpenedWorkshopIds);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task SelectingItemAndReturningPreservesResultRowsAndSearchContext()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "地图";
        await vm.SearchWorkshopAsync();
        var originalRows = vm.WorkshopResults.ToArray();

        // 内存 fake 返回已完成的 Task，命令在当前线程完成，不依赖 UI 调度或网络时序。
        originalRows[0].OpenCommand.Execute(null);
        await vm.PreviewLoadingTask;
        Assert.True(vm.HasWorkshopItem);
        Assert.Equal(101UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(555, vm.WorkshopItem.Subscriptions);
        Assert.Equal("详情标题", vm.WorkshopTitle);
        Assert.Equal("详细说明 & 内容", vm.WorkshopDescription);
        Assert.Equal(new[] { 101UL }, fixture.Source.RequestedIds);
        Assert.Equal(originalRows, vm.WorkshopResults);

        vm.BackToWorkshopCommand.Execute(null);
        Assert.False(vm.HasWorkshopItem);
        Assert.False(vm.HasWorkshopPreview);
        Assert.True(vm.HasWorkshopResults);
        Assert.Equal("地图", vm.WorkshopInput);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.Same(originalRows[0], vm.WorkshopResults[0]);
        Assert.Single(fixture.Browser.Requests);
        Assert.Empty(fixture.Navigator.OpenedWorkshopIds);
    }

    [Fact]
    public async Task ExplicitDetailLookupUsesProvidedReference()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.LoadWorkshopDetailAsync("https://steamcommunity.com/sharedfiles/filedetails/?id=202");
        Assert.Equal(202UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(new[] { 202UL }, fixture.Source.RequestedIds);
        Assert.Empty(fixture.Browser.Requests);
    }

    [Fact]
    public async Task NextPageKeepsSubmittedSearchWhenInputsHaveUnsubmittedEdits()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "已提交";
        vm.SelectedWorkshopSort = vm.WorkshopSorts.Single(option => option.Value == WorkshopSort.RecentlyUpdated);
        vm.SelectedWorkshopCategory = new WorkshopFilter("事件", "Events");
        vm.SelectedWorkshopVersion = new WorkshopFilter("1.17", "1.17");
        await vm.SearchWorkshopAsync();

        vm.WorkshopInput = "未提交";
        vm.SelectedWorkshopSort = vm.WorkshopSorts.Single(option => option.Value == WorkshopSort.MostRecent);
        vm.SelectedWorkshopCategory = new WorkshopFilter("画面", "Graphics");
        vm.SelectedWorkshopVersion = new WorkshopFilter("1.16", "1.16");
        vm.NextWorkshopPageCommand.Execute(null);

        Assert.Equal(2, fixture.Browser.Requests.Count);
        Assert.Equal(fixture.Browser.Requests[0] with { Page = 2 }, fixture.Browser.Requests[1]);
        Assert.Equal("第 2 / 3 页", vm.WorkshopPageLabel);
        Assert.Null(vm.LastError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task TypedPageNumberJumpsAndClampsToAvailablePages()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "页码";
        await vm.SearchWorkshopAsync();
        Assert.Equal("1", vm.WorkshopPageInput);

        vm.WorkshopPageInput = "3";
        vm.GoToWorkshopPageCommand.Execute(null);
        await vm.WorkshopLoadingTask;
        Assert.Equal(3, fixture.Browser.Requests[^1].Page);
        Assert.Equal("第 3 / 3 页", vm.WorkshopPageLabel);
        Assert.Equal("3", vm.WorkshopPageInput);

        var requests = fixture.Browser.Requests.Count;
        vm.WorkshopPageInput = "99";
        vm.GoToWorkshopPageCommand.Execute(null);
        Assert.Equal(requests, fixture.Browser.Requests.Count);
        Assert.Equal("3", vm.WorkshopPageInput);

        vm.WorkshopPageInput = "not a page";
        vm.GoToWorkshopPageCommand.Execute(null);
        Assert.Equal("3", vm.WorkshopPageInput);
    }

    [Fact]
    public async Task ReturningToFirstPageAlsoKeepsSubmittedSearch()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "已提交";
        await vm.SearchWorkshopAsync();
        vm.NextWorkshopPageCommand.Execute(null);
        vm.WorkshopInput = "未提交";
        vm.SelectedWorkshopSort = vm.WorkshopSorts.Single(option => option.Value == WorkshopSort.MostRecent);
        vm.PreviousWorkshopPageCommand.Execute(null);

        Assert.Equal(2, fixture.Browser.Requests.Count);
        Assert.Equal(fixture.Browser.Requests[0] with { Page = 2 }, fixture.Browser.Requests[1]);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.Equal("未提交", vm.WorkshopInput);
        Assert.Equal(WorkshopSort.MostRecent, vm.SelectedWorkshopSort?.Value);

        // 返回第一页命中缓存；显式刷新仍使用已提交的查询，不能带入未提交的编辑。
        await vm.SearchWorkshopAsync(useActiveQuery: true, forceRefresh: true);
        Assert.Equal(3, fixture.Browser.Requests.Count);
        Assert.Equal(fixture.Browser.Requests[0], fixture.Browser.Requests[2]);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
    }

    [Fact]
    public async Task ResetRestoresDefaultConditionsAndSearchesFirstPage()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.WorkshopInput = "关键字";
        vm.SelectedWorkshopSort = vm.WorkshopSorts.Single(option => option.Value == WorkshopSort.MostRecent);
        vm.SelectedWorkshopCategory = new WorkshopFilter("事件", "Events");
        vm.SelectedWorkshopVersion = new WorkshopFilter("1.17", "1.17");
        await vm.SearchWorkshopAsync();
        vm.NextWorkshopPageCommand.Execute(null);

        await vm.ResetWorkshopAsync();

        Assert.Equal(new WorkshopSearchRequest(), fixture.Browser.Requests[^1]);
        Assert.Equal("", vm.WorkshopInput);
        Assert.Null(vm.SelectedWorkshopCategory?.Value);
        Assert.Null(vm.SelectedWorkshopVersion?.Value);
        Assert.Equal(WorkshopSort.Trending, vm.SelectedWorkshopSort?.Value);
        Assert.Equal("第 1 / 3 页", vm.WorkshopPageLabel);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task BrowserRefreshFailureKeepsCachedRowsAndShowsErrorInsteadOfEmptyResults()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.SearchWorkshopAsync();
        Assert.NotEmpty(vm.WorkshopResults);
        var originalIds = vm.WorkshopResults.Select(row => row.Item.PublishedFileId).ToArray();
        fixture.Browser.Failure = new HttpRequestException("测试工坊不可用");

        await vm.SearchWorkshopAsync(forceRefresh: true);

        Assert.Equal(originalIds, vm.WorkshopResults.Select(row => row.Item.PublishedFileId));
        Assert.True(vm.HasWorkshopResults);
        Assert.False(vm.NoWorkshopResults);
        Assert.False(vm.WorkshopNotLoaded);
        Assert.True(vm.HasError);
        Assert.Null(vm.LastError);
        Assert.Contains("测试工坊不可用", vm.ErrorMessage);
        Assert.Contains("保留上次", vm.WorkshopDataStatus);
        Assert.Equal(2, fixture.Browser.Requests.Count);
        Assert.True(vm.NextWorkshopPageCommand.CanExecute(null));
        Assert.False(vm.PreviousWorkshopPageCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
    }

    [Fact]
    public async Task GenuineEmptyBrowserResultShowsNoMatchesWithoutAnError()
    {
        var fixture = new Fixture { EmptyResults = true };
        using var vm = fixture.Create();
        await vm.SearchWorkshopAsync();
        Assert.Empty(vm.WorkshopResults);
        Assert.True(vm.NoWorkshopResults);
        Assert.False(vm.WorkshopNotLoaded);
        Assert.False(vm.HasError);
        Assert.Contains("没有可显示", vm.Status);
        Assert.False(vm.NextWorkshopPageCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("[h1]标题[/h1][b]正文[/b] &amp; 内容", "标题\n正文 & 内容")]
    [InlineData("[url=https://example.invalid]查看说明[/url]", "查看说明")]
    [InlineData("前面[img]https://example.invalid/image.png[/img]后面", "前面后面")]
    [InlineData("[list][*]第一个[*]第二个[/list]", "• 第一个\n• 第二个")]
    [InlineData("  普通文本 &lt;说明&gt;  ", "普通文本 <说明>")]
    public void ConvertsWorkshopBbCodeToReadablePlainText(string formatted, string expected) =>
        Assert.Equal(expected, WorkshopText.Plain(formatted));

    private sealed class Fixture
    {
        public BrowserFake Browser { get; } = new();
        public SourceFake Source { get; } = new();
        public NavigatorFake Navigator { get; } = new();
        public bool EmptyResults { get; init; }
        public TaskCompletionSource? LocalDiscoveryGate { get; init; }

        public MainWindowViewModel Create()
        {
            var ports = new MemoryPorts(LocalDiscoveryGate);
            Browser.EmptyResults = EmptyResults;
            return new MainWindowViewModel(new WorkspaceService(ports, ports, ports, ports),
                new WorkshopService(Source, Browser), Navigator) { WorkshopTargetVersion = "1.17.1" };
        }
    }

    private sealed class BrowserFake : IWorkshopBrowser
    {
        public List<WorkshopSearchRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public bool EmptyResults { get; set; }
        public TaskCompletionSource FirstSearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            FirstSearchStarted.TrySetResult();
            if (Failure is not null) return Task.FromException<WorkshopBrowsePage>(Failure);
            IReadOnlyList<WorkshopItem> items = EmptyResults ? [] :
            [
                MakeItem(101) with { Title = "地图扩展", Description = "[b]新增地图[/b]\n内容", Subscriptions = 555 },
                MakeItem(202) with { Title = "事件与汉化" }
            ];
            return Task.FromResult(new WorkshopBrowsePage(items, request.Page, EmptyResults ? 1 : 3,
                EmptyResults ? 0 : 5, ["Events", "Graphics"], ["1.17", "1.16"]));
        }
    }

    private sealed class SourceFake : IWorkshopSource
    {
        public List<ulong> RequestedIds { get; } = [];
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedIds.Add(publishedFileId);
            return Task.FromResult(MakeItem(publishedFileId));
        }
    }

    private static WorkshopItem MakeItem(ulong id) => new(id, Ck3.AppId, "详情标题", "[b]详细说明[/b] &amp; 内容", null,
        new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"), DateTimeOffset.UnixEpoch, 1024, ["Events", "1.17"], null);

    private sealed class MemoryPorts(TaskCompletionSource? discoveryGate = null) : IGameLocator, IModScanner, IProfileStore, ISettingsStore
    {
        public async Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default)
        {
            if (discoveryGate is not null) await discoveryGate.Task.WaitAsync(cancellationToken);
            return new GameEnvironment(null, "fixture-data", "1.17.1", [], []);
        }
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModScanResult([], []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NavigatorFake : IExternalNavigator
    {
        public List<ulong?> OpenedWorkshopIds { get; } = [];
        public void LaunchGame() => throw new InvalidOperationException("工坊浏览不应启动游戏。");
        public void OpenWorkshop(ulong? publishedFileId = null) => OpenedWorkshopIds.Add(publishedFileId);
        public void OpenDirectory(string path) => throw new InvalidOperationException("工坊浏览不应打开用户目录。");
    }
}
