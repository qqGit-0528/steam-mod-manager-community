using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class WorkshopCacheFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningAListReleaseShowsItsSummaryAndEveryChoiceBeforeTheFullDetailArrives()
    {
        var fixture = new Fixture();
        var reply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (_, _) => reply.Task;
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.SelectedRelease = row.Versions.Single(option => option.Choice.Item.PublishedFileId == 101);

        row.OpenCommand.Execute(null);
        var loading = vm.WorkshopLoadingTask;

        Assert.False(loading.IsCompleted);
        Assert.Equal("Campaign", vm.WorkshopTitle);
        Assert.Equal("旧发布的列表简介", vm.WorkshopDescription);
        Assert.Equal("简介", vm.WorkshopDescriptionLabel);
        Assert.Equal(new[] { 101UL, 202UL }, vm.DetailVersions.Select(option => option.Choice.Item.PublishedFileId).Order());
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopLoading);
        Assert.True(vm.WorkshopRefreshing);
        Assert.True(vm.WorkshopWorking);
        Assert.True(vm.BackToWorkshopCommand.CanExecute(null));
        Assert.False(vm.CanDownloadWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        vm.DownloadWorkshopCommand.Execute(null);
        Assert.Empty(fixture.OpenedIds);

        reply.Complete(fixture.Items[0] with { Description = "[b]作者完整介绍[/b]", RequiredItemIds = [999] });
        await Finish(loading);

        Assert.Equal("作者完整介绍", vm.WorkshopDescription);
        Assert.Equal("作者介绍", vm.WorkshopDescriptionLabel);
        Assert.Equal(new[] { 999UL }, vm.WorkshopItem!.RequiredItemIds);
        Assert.Equal(2, vm.DetailVersions.Count);
        Assert.True(vm.CanDownloadWorkshopItem);
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
        Assert.Null(vm.ErrorMessage);
        vm.DownloadWorkshopCommand.Execute(null);
        Assert.Empty(fixture.OpenedIds);

        vm.BackToWorkshopCommand.Execute(null);
        Assert.False(vm.HasWorkshopItem);
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Item.PublishedFileId);
        row.OpenCommand.Execute(null);
        await Finish(vm.WorkshopLoadingTask);

        Assert.Equal(new[] { 101UL }, fixture.DetailRequests);
        Assert.Equal("作者完整介绍", vm.WorkshopDescription);
        Assert.Equal("作者介绍", vm.WorkshopDescriptionLabel);
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.Equal(2, vm.DetailVersions.Count);
        Assert.True(vm.CanDownloadWorkshopItem);
        Assert.False(vm.WorkshopWorking);
    }

    [Fact]
    public async Task ReusingARefreshedRowOpensItsCurrentRevisionAndCurrentReleaseChoicesImmediately()
    {
        var fixture = new Fixture();
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.SelectedRelease = row.Versions.Single(option => option.Choice.Item.PublishedFileId == 101);
        var newer = fixture.Items[0] with { Description = "新修订的列表简介", UpdatedAt = fixture.Items[0].UpdatedAt.AddDays(3) };
        var added = Item(303, "本次新出现的发布", 5);
        fixture.FetchPage = (request, _) => Task.FromResult(Page(request, [newer, fixture.Items[1], added]));

        await vm.SearchWorkshopAsync(forceRefresh: true);

        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Item.PublishedFileId);
        Assert.Equal("新修订的列表简介", row.Summary);
        var reply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (_, _) => reply.Task;
        row.OpenCommand.Execute(null);
        var loading = vm.WorkshopLoadingTask;

        Assert.False(loading.IsCompleted);
        Assert.Equal("新修订的列表简介", vm.WorkshopDescription);
        Assert.Equal(newer.UpdatedAt, vm.WorkshopItem!.UpdatedAt);
        Assert.Equal(new[] { 101UL, 202UL, 303UL }, vm.DetailVersions.Select(option => option.Choice.Item.PublishedFileId).Order());
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.False(vm.CanDownloadWorkshopItem);
        reply.Complete(newer with { Description = "新修订的完整介绍" });
        await Finish(loading);
        vm.BackToWorkshopCommand.Execute(null);
        row.OpenCommand.Execute(null);
        await Finish(vm.WorkshopLoadingTask);

        Assert.Single(fixture.DetailRequests);
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Item.PublishedFileId);
        Assert.Equal("新修订的完整介绍", vm.WorkshopDescription);
        Assert.Equal(3, vm.DetailVersions.Count);
        Assert.True(vm.CanDownloadWorkshopItem);
    }

    [Fact]
    public async Task ExpiredDetailRemainsReadableWhenItsRefreshFailsButCannotBeSubscribed()
    {
        var fixture = new Fixture();
        fixture.FetchDetail = (id, _) => Task.FromResult(fixture.Items.Single(item => item.PublishedFileId == id) with { Description = "已缓存的作者介绍" });
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.OpenCommand.Execute(null);
        await Finish(vm.WorkshopLoadingTask);
        var fetchedAt = (await fixture.Cache.ReadItemAsync(202, Token))!.FetchedAt;
        Assert.True(vm.DownloadWorkshopCommand.CanExecute(null));

        fixture.Clock.Advance(WorkshopCachePolicy.FreshFor);
        Assert.False(vm.CanDownloadWorkshopItem);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        vm.BackToWorkshopCommand.Execute(null);
        var reply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (_, _) => reply.Task;
        row.OpenCommand.Execute(null);
        var loading = vm.WorkshopLoadingTask;

        Assert.False(loading.IsCompleted);
        Assert.Equal("已缓存的作者介绍", vm.WorkshopDescription);
        Assert.Equal("作者介绍", vm.WorkshopDescriptionLabel);
        Assert.True(vm.WorkshopRefreshing);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanDownloadWorkshopItem);
        reply.Fail(new IOException("受控离线"));
        await Finish(loading);

        Assert.Equal("已缓存的作者介绍", vm.WorkshopDescription);
        Assert.Equal(202UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal(2, vm.DetailVersions.Count);
        Assert.False(vm.WorkshopWorking);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));
        Assert.Contains("受控离线", vm.ErrorMessage);
        Assert.Contains("保留已有资料", vm.WorkshopDataStatus);
        Assert.Equal(fetchedAt, (await fixture.Cache.ReadItemAsync(202, Token))!.FetchedAt);
        Assert.Equal(new[] { 202UL, 202UL }, fixture.DetailRequests);
    }

    [Fact]
    public async Task ReturningToTheListDiscardsALateDetailAndKeepsTheChosenRelease()
    {
        var fixture = new Fixture();
        var reply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (_, _) => reply.Task; // Deliberately ignores cancellation.
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.SelectedRelease = row.Versions.Single(option => option.Choice.Item.PublishedFileId == 101);
        row.OpenCommand.Execute(null);
        var oldLoading = vm.WorkshopLoadingTask;

        vm.BackToWorkshopCommand.Execute(null);
        await Finish(oldLoading);
        reply.Complete(fixture.Items[0] with { Description = "不应回填的迟到介绍" });

        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.False(vm.WorkshopWorking);
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Item.PublishedFileId);
        Assert.Equal("旧发布的列表简介", row.Summary);
        Assert.False(vm.DownloadWorkshopCommand.CanExecute(null));

        fixture.FetchDetail = (id, _) => Task.FromResult(fixture.Items.Single(item => item.PublishedFileId == id) with { Description = "重新进入后的介绍" });
        row.OpenCommand.Execute(null);
        await Finish(vm.WorkshopLoadingTask);
        Assert.Equal(new[] { 101UL, 101UL }, fixture.DetailRequests);
        Assert.Equal("重新进入后的介绍", vm.WorkshopDescription);
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
    }

    [Fact]
    public async Task SwitchingReleaseWhileLoadingPreventsThePreviousReleaseFromReplacingTheCurrentDetail()
    {
        var fixture = new Fixture();
        var oldReply = new Deferred<WorkshopItem>();
        var newReply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (id, _) => id == 202 ? oldReply.Task : newReply.Task;
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.OpenCommand.Execute(null);
        var oldLoading = vm.WorkshopLoadingTask;

        vm.SelectedDetailVersion = vm.DetailVersions.Single(option => option.Choice.Item.PublishedFileId == 101);
        var currentLoading = vm.WorkshopLoadingTask;
        await Finish(oldLoading);
        Assert.Equal(101UL, vm.WorkshopItem!.PublishedFileId);
        Assert.Equal("旧发布的列表简介", vm.WorkshopDescription);
        Assert.False(vm.CanDownloadWorkshopItem);
        newReply.Complete(fixture.Items[0] with { Description = "当前选择的作者介绍" });
        await Finish(currentLoading);
        oldReply.Complete(fixture.Items[1] with { Description = "前一个发布的迟到介绍" });

        Assert.Equal(new[] { 202UL, 101UL }, fixture.DetailRequests);
        Assert.Equal(101UL, vm.WorkshopItem.PublishedFileId);
        Assert.Equal(101UL, vm.SelectedDetailVersion!.Choice.Item.PublishedFileId);
        Assert.Equal(101UL, row.Item.PublishedFileId);
        Assert.Equal("当前选择的作者介绍", vm.WorkshopDescription);
        Assert.True(vm.CanDownloadWorkshopItem);
        Assert.False(vm.WorkshopWorking);
        Assert.Null(vm.ErrorMessage);
        vm.BackToWorkshopCommand.Execute(null);
        Assert.Same(row, Assert.Single(vm.WorkshopResults));
        Assert.Equal(101UL, row.Item.PublishedFileId);
    }

    [Fact]
    public async Task StartingAnotherQueryDiscardsTheOldDetailWithoutRestoringItsOldList()
    {
        var fixture = new Fixture();
        var reply = new Deferred<WorkshopItem>();
        fixture.FetchDetail = (_, _) => reply.Task;
        var other = Item(303, "另一项目简介", 3) with { Title = "Another campaign", CreatorId = "other-author" };
        fixture.FetchPage = (request, _) => Task.FromResult(Page(request, request.SearchText == "另一查询" ? [other] : fixture.Items));
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        Assert.Single(vm.WorkshopResults).OpenCommand.Execute(null);
        var oldLoading = vm.WorkshopLoadingTask;

        vm.WorkshopInput = "另一查询";
        await vm.SearchWorkshopAsync();
        await Finish(oldLoading);
        reply.Complete(fixture.Items[1] with { Description = "旧查询的迟到介绍" });

        Assert.False(vm.HasWorkshopItem);
        Assert.Empty(vm.DetailVersions);
        Assert.Equal(303UL, Assert.Single(vm.WorkshopResults).Item.PublishedFileId);
        Assert.Equal("另一项目简介", vm.WorkshopResults[0].Summary);
        Assert.False(vm.IsBusy);
        Assert.False(vm.WorkshopWorking);
        Assert.False(vm.CanDownloadWorkshopItem);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task AnOlderDelayedQueryCannotOverwriteTheNewerQueryResults()
    {
        var fixture = new Fixture();
        var reply = new Deferred<WorkshopBrowsePage>();
        var other = Item(303, "新查询简介", 3) with { Title = "New query result", CreatorId = "another-author" };
        fixture.FetchPage = (request, _) => request.SearchText == "旧查询" ? reply.Task : Task.FromResult(Page(request, [other]));
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        vm.WorkshopInput = "旧查询";
        var oldLoading = vm.SearchWorkshopAsync();
        Assert.False(oldLoading.IsCompleted);
        Assert.False(vm.IsBusy);
        Assert.True(vm.WorkshopLoading);

        vm.WorkshopInput = "新查询";
        await vm.SearchWorkshopAsync();
        await Finish(oldLoading);
        reply.Complete(Page(new WorkshopSearchRequest("旧查询"), fixture.Items));

        Assert.Equal(new[] { "旧查询", "新查询" }, fixture.BrowseRequests.Select(request => request.SearchText));
        Assert.Equal(303UL, Assert.Single(vm.WorkshopResults).Item.PublishedFileId);
        Assert.Equal("New query result", vm.WorkshopResults[0].Title);
        Assert.False(vm.WorkshopWorking);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CachedAndRefreshedImageBytesBecomeIndependentListAndDetailBitmaps()
    {
        var images = new UpdatingImages();
        var item = Item(101, "有封面的简介", 1) with { PreviewImageUrl = "https://images.example.test/101.png" };
        var fixture = new Fixture([item]) { Images = images };
        using var vm = fixture.Create();
        await vm.InitializeAsync(Token);
        await vm.SearchWorkshopAsync();
        var row = Assert.Single(vm.WorkshopResults);
        row.OpenCommand.Execute(null);
        await Finish(vm.WorkshopLoadingTask);

        var oldList = Assert.IsType<Bitmap>(row.Preview);
        var oldDetail = Assert.IsType<Bitmap>(vm.WorkshopPreview);
        Assert.NotSame(oldList, oldDetail);
        var oldListBytes = Encode(oldList);
        var oldDetailBytes = Encode(oldDetail);
        Assert.False(vm.PreviewLoadingTask.IsCompleted);
        Assert.False(vm.WorkshopWorking); // Image refresh does not hold metadata navigation busy.
        Assert.True(vm.CanDownloadWorkshopItem);
        var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(row.Preview) && row.Preview is not null) listChanged.TrySetResult(); };
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.WorkshopPreview) && vm.WorkshopPreview is not null) detailChanged.TrySetResult(); };

        images.Replace();
        await Finish(Task.WhenAll(listChanged.Task, detailChanged.Task, vm.PreviewLoadingTask));

        Assert.NotSame(oldList, row.Preview);
        Assert.NotSame(oldDetail, vm.WorkshopPreview);
        Assert.NotSame(row.Preview, vm.WorkshopPreview);
        Assert.False(oldListBytes.SequenceEqual(Encode(row.Preview!)));
        Assert.False(oldDetailBytes.SequenceEqual(Encode(vm.WorkshopPreview!)));
        Assert.True(vm.CanDownloadWorkshopItem);
        var listAfterRefresh = row.Preview;
        vm.BackToWorkshopCommand.Execute(null);
        Assert.Null(vm.WorkshopPreview);
        Assert.Same(listAfterRefresh, row.Preview);
        Assert.True(Encode(row.Preview!).Length > 0);
    }

    private static Task Finish(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static WorkshopItem Item(ulong id, string description, int day) => new(id, Ck3.AppId, "Campaign", description, null,
        new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"), DateTimeOffset.UnixEpoch.AddDays(day),
        1024, ["1.20"], null, CreatorId: "author", Subscriptions: 123);

    private static WorkshopBrowsePage Page(WorkshopSearchRequest request, IReadOnlyList<WorkshopItem> items) =>
        new(items, request.Page, 3, items.Count, [], ["1.20"]);

    private sealed class Deferred<T>
    {
        // Inline continuations let Complete return only after an uncooperative source's
        // late result has propagated through the service's cancellation checks.
        private readonly TaskCompletionSource<T> _reply = new();
        public Task<T> Task => _reply.Task;
        public void Complete(T value) => _reply.SetResult(value);
        public void Fail(Exception error) => _reply.SetException(error);
    }

    private sealed class Fixture : IGameLocator, IModScanner, IProfileStore, ISettingsStore, IWorkshopSource, IWorkshopBrowser, IExternalNavigator
    {
        public Fixture(IReadOnlyList<WorkshopItem>? items = null) =>
            Items = items ?? [Item(101, "旧发布的列表简介", 1), Item(202, "新发布的列表简介", 2)];
        public IReadOnlyList<WorkshopItem> Items { get; }
        public Clock Clock { get; } = new();
        public MemoryWorkshopCacheStore Cache { get; } = new();
        public IWorkshopImageSource? Images { get; init; }
        public Func<ulong, CancellationToken, Task<WorkshopItem>>? FetchDetail { get; set; }
        public Func<WorkshopSearchRequest, CancellationToken, Task<WorkshopBrowsePage>>? FetchPage { get; set; }
        public List<ulong> DetailRequests { get; } = [];
        public List<WorkshopSearchRequest> BrowseRequests { get; } = [];
        public List<ulong?> OpenedIds { get; } = [];
        public MainWindowViewModel Create() => new(new WorkspaceService(this, this, this, this),
            new WorkshopService(this, this, Images, cache: Cache, timeProvider: Clock), this, Clock);
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            DetailRequests.Add(publishedFileId);
            return FetchDetail?.Invoke(publishedFileId, cancellationToken) ?? Task.FromResult(Items.Single(item => item.PublishedFileId == publishedFileId));
        }
        public Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
        {
            BrowseRequests.Add(request);
            return FetchPage?.Invoke(request, cancellationToken) ?? Task.FromResult(Page(request, Items));
        }
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, "fixture-data", "1.20.3", [], []));
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModScanResult([], []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LaunchGame() => throw new InvalidOperationException("缓存测试不应启动游戏。");
        public void OpenWorkshop(ulong? publishedFileId = null) => OpenedIds.Add(publishedFileId);
        public void OpenDirectory(string path) => throw new InvalidOperationException("缓存测试不应打开用户目录。");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class UpdatingImages : IWorkshopImageSource, IWorkshopPreviewUpdates
    {
        private static readonly byte[] Red = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        private static readonly byte[] Blue = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==");
        private readonly Deferred<byte[]> _replacement = new();
        public void Replace() => _replacement.Complete(Blue);
        public Task<byte[]> GetPreviewAsync(string url, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("图片必须使用逐次更新流。");
        public async IAsyncEnumerable<WorkshopPreviewUpdate> ObservePreviewAsync(string url,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new WorkshopPreviewUpdate(Red, IsStale: true, IsRefreshing: true);
            yield return new WorkshopPreviewUpdate(await _replacement.Task);
        }
    }
}
