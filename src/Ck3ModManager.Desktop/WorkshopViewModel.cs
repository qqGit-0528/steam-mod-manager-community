using static Ck3ModManager.Core.AppText;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record WorkshopFilter(string Label, string? Value)
{
    public override string ToString() => Label;
}

public sealed record WorkshopSortOption(string Label, WorkshopSort Value)
{
    public override string ToString() => Label;
}

public static class WorkshopText
{
    public static string Plain(string text)
    {
        text = Regex.Replace(text, @"\[img\].*?\[/img\]", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\[\*\]", "\n• ", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\[/?(?:h[1-6]|list|olist|table|tr)(?:=[^\]]*)?\]", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\[/?[a-zA-Z][^\]\r\n]*\]", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        return WebUtility.HtmlDecode(text).Trim();
    }
}

public sealed class WorkshopVersionOption(WorkshopReleaseChoice choice, bool isRecommended) : ObservableObject
{
    public WorkshopReleaseChoice Choice { get; } = choice;
    public bool IsRecommended { get; } = isRecommended;
    private string GameName => GameCatalog.FindByAppId(Choice.Item.ConsumerAppId)?.ShortName ?? T("Game");
    public string Label => $"{(IsRecommended ? T("Recommended") : "")}{(Choice.ModVersion is { } version ? $"Mod {version} · " : "")}{GameName} {(Choice.GameVersions.Count > 0 ? string.Join(" / ", Choice.GameVersions) : T("Undeclared"))} · {Choice.Item.UpdatedAt.ToLocalTime():yy-MM-dd}";
    public string Detail => T("WorkshopID", CompatibilityDisplay.Label(Choice.Compatibility), Choice.Item.PublishedFileId);
    public override string ToString() => Label;
}

public sealed class WorkshopModRow : ObservableObject, IDisposable
{
    private Bitmap? _preview;
    private bool _disposed;
    private WorkshopVersionOption? _selectedRelease;
    private readonly WorkshopService _service;
    private CancellationToken _parentToken;
    private bool _canLoadPreview;
    private CancellationTokenSource? _previewOperation;
    private string? _previewDigest;

    public WorkshopModRow(WorkshopProject project, string? targetVersion, WorkshopService service, Func<WorkshopItem, Task> open)
    {
        Project = project;
        _service = service;
        SetTargetVersion(targetVersion);
        OpenCommand = new AsyncCommand(() => open(Item), () => !_disposed && SelectedRelease is not null);
    }

    public WorkshopProject Project { get; private set; }
    public ObservableCollection<WorkshopVersionOption> Versions { get; } = [];
    public WorkshopVersionOption? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            var previousId = _selectedRelease?.Choice.Item.PublishedFileId;
            var previousImage = _selectedRelease?.Choice.Item.PreviewImageUrl;
            if (value is null || !Versions.Contains(value) || !Set(ref _selectedRelease, value)) return;
            var imageChanged = previousId != value.Choice.Item.PublishedFileId || previousImage != value.Choice.Item.PreviewImageUrl;
            if (imageChanged) { CancelImage(); Preview = null; }
            foreach (var property in new[] { nameof(Item), nameof(GameShortName), nameof(Summary), nameof(Tags), nameof(Metadata), nameof(Recommendation), nameof(IsCompatibilityMatched), nameof(IsCompatibilityMismatch) }) Changed(property);
            if (_canLoadPreview && imageChanged) PreviewLoadingTask = LoadPreviewAsync(_parentToken);
        }
    }
    public WorkshopItem Item => SelectedRelease!.Choice.Item;
    public string GameShortName => GameCatalog.FindByAppId(SelectedRelease?.Choice.Item.ConsumerAppId ?? _service.Game.AppId)?.ShortName ?? _service.Game.ShortName;
    public string Title => Project.Title;
    public string Summary => WorkshopText.Plain(Item.Description).Replace('\n', ' ');
    public string Tags => string.Join(" · ", Item.Tags.Take(4));
    public string Metadata => T("UpdatedSteam", (Item.Subscriptions is { } count ? T("Subscribers", count) : ""), Item.UpdatedAt.ToLocalTime());
    public string VersionCount => T("Releases", Versions.Count);
    public string Recommendation => CompatibilityDisplay.Label(SelectedRelease?.Choice.Compatibility ?? CompatibilityStatus.Unknown);
    public bool IsCompatibilityMatched => SelectedRelease?.Choice.Compatibility == CompatibilityStatus.DeclaredMatch;
    public bool IsCompatibilityMismatch => SelectedRelease?.Choice.Compatibility == CompatibilityStatus.DeclaredMismatch;
    public Bitmap? Preview { get => _preview; private set { if (ReferenceEquals(_preview, value)) return; _preview?.Dispose(); _preview = value; Changed(); Changed(nameof(HasPreview)); } }
    public bool HasPreview => Preview is not null;
    public ICommand OpenCommand { get; }
    public Task PreviewLoadingTask { get; private set; } = Task.CompletedTask;

    public void UpdateProject(WorkshopProject project, string? targetVersion)
    {
        Project = project;
        SetTargetVersion(targetVersion);
        Changed(nameof(Project));
        Changed(nameof(Title));
    }

    public void SetTargetVersion(string? targetVersion)
    {
        var selectedId = _selectedRelease?.Choice.Item.PublishedFileId;
        Versions.Clear();
        var ranked = WorkshopCatalog.Rank(Project, targetVersion).ToArray();
        for (var i = 0; i < ranked.Length; i++) Versions.Add(new WorkshopVersionOption(ranked[i],
            i == 0 && (_service.Game.RequiresDeclaredWorkshopCompatibility || ranked[i].Compatibility == CompatibilityStatus.DeclaredMatch)));
        var selected = Versions.FirstOrDefault(option => option.Choice.Item.PublishedFileId == selectedId)
            ?? Versions.FirstOrDefault(option => option.Choice.Compatibility == CompatibilityStatus.DeclaredMatch)
            ?? Versions.FirstOrDefault();
        if (selected is null) { _selectedRelease = null; CancelImage(); Preview = null; }
        else SelectedRelease = selected;
        Changed(nameof(VersionCount));
        if (OpenCommand is AsyncCommand command) command.Refresh();
    }

    public async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        _parentToken = cancellationToken;
        _canLoadPreview = true;
        if (_disposed || SelectedRelease is null || string.IsNullOrWhiteSpace(Item.PreviewImageUrl)) return;
        CancelImage();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _previewOperation = operation;
        var selectedId = Item.PublishedFileId;
        var url = Item.PreviewImageUrl;
        try
        {
            await foreach (var update in _service.ObservePreviewAsync(url, operation.Token))
            {
                if (operation.IsCancellationRequested || _disposed || !ReferenceEquals(_previewOperation, operation) ||
                    SelectedRelease?.Choice.Item.PublishedFileId != selectedId || Item.PreviewImageUrl != url) return;
                using var stream = update.OpenRead();
                if (stream is null) continue;
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                if (Preview is not null && _previewDigest == digest) continue;
                stream.Position = 0;
                var bitmap = Bitmap.DecodeToWidth(stream, 120);
                if (operation.IsCancellationRequested || _disposed || !ReferenceEquals(_previewOperation, operation) ||
                    SelectedRelease?.Choice.Item.PublishedFileId != selectedId || Item.PreviewImageUrl != url) bitmap.Dispose();
                else { Preview = bitmap; _previewDigest = digest; }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Keep an existing cover if refresh fails. */ }
        finally { if (ReferenceEquals(_previewOperation, operation)) _previewOperation = null; }
    }

    private void CancelImage() { _previewOperation?.Cancel(); _previewOperation = null; }
    public void Dispose() { _disposed = true; CancelImage(); Preview = null; }
}

public sealed partial class MainWindowViewModel
{
    private bool _workshopPageRefreshPending;
    private WorkshopSearchRequest? _lastSubmittedWorkshopSearch;
    private bool _hasBrowsePage;
    private WorkshopSearchRequest? _activeSearch;
    private int _workshopPage = 1;
    private int _workshopTotalPages = 1;
    private string _workshopPageInput = "";
    private int _workshopTotalCount;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _detailCancellation;
    private Bitmap? _workshopPreview;
    private WorkshopFilter? _selectedCategory;
    private WorkshopFilter? _selectedVersion;
    private string _categoryInput = T("AllCategories");
    private string _versionInput = T("AllVersions");
    private WorkshopSortOption? _selectedSort;
    private string? _targetVersionOverride;
    private WorkshopProject? _detailProject;
    private WorkshopVersionOption? _selectedDetailVersion;
    private bool _settingDetailVersions;
    private IReadOnlyList<WorkshopProject> _workshopProjects = [];
    private IReadOnlyList<string> _workshopVersionTags = [];
    private readonly Dictionary<string, ulong> _workshopSelections = [];
    private bool _workshopSearchNeedsRefresh;
    private readonly TimeProvider _workshopMetadataTimeProvider;
    private readonly Dictionary<ulong, VerifiedWorkshopMetadata> _verifiedWorkshopMetadata = [];
    // 当前页最多展示 10 个项目，兼容项优先；不预取其他 Steam 页。
    private const int WorkshopPageItemLimit = 10;
    private sealed record VerifiedWorkshopMetadata(DateTimeOffset Revision, IReadOnlyList<string> Tags, DateTimeOffset CheckedAt);
    private CancellationTokenSource? _workshopOperation;
    private long _workshopRequestId;
    private bool _workshopLoading;
    private bool _workshopRefreshing;
    private bool _detailHasFullDescription;
    private bool _detailIsFresh;
    private DateTimeOffset? _detailFetchedAt;
    private string? _workshopDataStatusKey;
    private string? _detailPreviewUrl;
    private ulong? _detailPreviewItemId;
    private string? _detailPreviewDigest;

    public bool WorkshopLoading { get => _workshopLoading; private set { if (Set(ref _workshopLoading, value)) NotifyWorkshopActivity(); } }
    public bool WorkshopRefreshing { get => _workshopRefreshing; private set { if (Set(ref _workshopRefreshing, value)) NotifyWorkshopActivity(); } }
    public bool WorkshopWorking => WorkshopLoading || WorkshopRefreshing;
    public bool CanCancelOperation => IsBusy || WorkshopWorking;
    public string WorkshopDataStatus => _workshopDataStatusKey is { } key ? T(key) : "";
    public bool HasWorkshopDataStatus => _workshopDataStatusKey is not null;

    private void SetWorkshopDataStatus(string? key)
    {
        if (!Set(ref _workshopDataStatusKey, key, nameof(WorkshopDataStatus))) return;
        Changed(nameof(HasWorkshopDataStatus));
    }
    public string WorkshopDescriptionLabel => _detailHasFullDescription ? T("AuthorDescription") : T("Summary");
    public Task WorkshopLoadingTask { get; private set; } = Task.CompletedTask;


    public ObservableCollection<WorkshopModRow> WorkshopResults { get; } = [];
    public ObservableCollection<WorkshopFilter> WorkshopCategories { get; } = [new(T("AllCategories"), null)];
    public ObservableCollection<WorkshopFilter> WorkshopVersions { get; } = [new(T("AllVersions"), null)];
    public ObservableCollection<WorkshopVersionOption> DetailVersions { get; } = [];
    public string WorkshopTargetVersion
    {
        get => _targetVersionOverride ?? _gameVersion ?? "";
        set { if (Set(ref _targetVersionOverride, (value ?? "").Trim())) UpdateWorkshopRecommendations(); }
    }
    public bool HasWorkshopTargetVersion => WorkshopTargetVersion.Contains('.') &&
        CompatibilityEvaluator.Evaluate("*", WorkshopTargetVersion).Status == CompatibilityStatus.DeclaredMatch;
    public bool CanLoadWorkshop => !Game.RequiresDeclaredWorkshopCompatibility || HasWorkshopTargetVersion;
    private string WorkshopVersionHint => T("TheCurrentVersionIsUnknownCheckThe", Game.ShortName);
    public string WorkshopRecommendationBasis => !Game.RequiresDeclaredWorkshopCompatibility
        ? HasWorkshopTargetVersion ? T("CurrentSeeTheAuthorSCompatibilityNotes", Game.ShortName, WorkshopTargetVersion)
            : T("GameVersionUnknownBrowseTheWorkshopAnd")
        : HasWorkshopTargetVersion ? T("Current", Game.ShortName, WorkshopTargetVersion) : WorkshopVersionHint;
    public string WorkshopLandingText => CanLoadWorkshop ? T("SearchByNameOrBrowseThisWeek") : WorkshopVersionHint;
    public bool CanDownloadWorkshopItem => Game.SupportsNativeWorkshopDownloads && HasWorkshopTargetVersion && WorkshopItem is not null &&
        _detailHasFullDescription && _detailIsFresh && _detailFetchedAt is { } fetchedAt &&
        WorkshopCachePolicy.IsFresh(fetchedAt, _workshopMetadataTimeProvider.GetUtcNow()) &&
        SelectedDetailVersion?.Choice.Item.PublishedFileId == WorkshopItem.PublishedFileId && IsDeclaredMatch(WorkshopItem);
    public WorkshopVersionOption? SelectedDetailVersion
    {
        get => _selectedDetailVersion;
        set
        {
            if (value is null || !DetailVersions.Contains(value) ||
                !CanOpenWorkshopItem(value.Choice.Item) || IsBusy && !_settingDetailVersions || !Set(ref _selectedDetailVersion, value)) return;
            Changed(nameof(WorkshopRecommendation));
            Changed(nameof(IsWorkshopCompatibilityMatched));
            Changed(nameof(IsWorkshopCompatibilityMismatch));
            if (!_settingDetailVersions && _detailProject is not null)
            {
                foreach (var row in WorkshopResults.Where(row => row.Project.Key == _detailProject.Key))
                    row.SelectedRelease = row.Versions.FirstOrDefault(option => option.Choice.Item.PublishedFileId == value.Choice.Item.PublishedFileId);
                _ = LoadProjectDetailAsync(value.Choice.Item.PublishedFileId.ToString(), value.Choice.Item.Subscriptions, _detailProject);
            }
        }
    }
    public string WorkshopRecommendation => CompatibilityDisplay.Label(SelectedDetailVersion?.Choice.Compatibility ?? CompatibilityStatus.Unknown);
    public bool IsWorkshopCompatibilityMatched => SelectedDetailVersion?.Choice.Compatibility == CompatibilityStatus.DeclaredMatch;
    public bool IsWorkshopCompatibilityMismatch => SelectedDetailVersion?.Choice.Compatibility == CompatibilityStatus.DeclaredMismatch;
    public string WorkshopReleaseAvailability => DetailVersions.Count > 1
        ? T("AvailableReleases", DetailVersions.Count)
        : "";
    public IReadOnlyList<WorkshopSortOption> WorkshopSorts { get; private set; } = LocalizedWorkshopSorts();

    private static IReadOnlyList<WorkshopSortOption> LocalizedWorkshopSorts() => [
        new(T("TrendingThisWeek"), WorkshopSort.Trending), new(T("MostSubscribed"), WorkshopSort.MostSubscribed),
        new(T("NewestReleases"), WorkshopSort.MostRecent), new(T("RecentlyUpdated"), WorkshopSort.RecentlyUpdated)];
    private void RefreshWorkshopLanguage()
    {
        var sort = SelectedWorkshopSort?.Value ?? WorkshopSort.Trending;
        WorkshopSorts = LocalizedWorkshopSorts();
        Changed(nameof(WorkshopSorts));
        SelectedWorkshopSort = WorkshopSorts.First(option => option.Value == sort);
        RefreshFilterLabel(WorkshopCategories, T("AllCategories"), ref _selectedCategory, ref _categoryInput,
            nameof(SelectedWorkshopCategory), nameof(WorkshopCategoryInput));
        RefreshFilterLabel(WorkshopVersions, T("AllVersions"), ref _selectedVersion, ref _versionInput,
            nameof(SelectedWorkshopVersion), nameof(WorkshopVersionInput));

        foreach (var row in WorkshopResults)
        {
            foreach (var option in row.Versions) option.RefreshLocalizedText();
            row.RefreshLocalizedText();
        }
        foreach (var option in DetailVersions) option.RefreshLocalizedText();
    }

    private void RefreshFilterLabel(ObservableCollection<WorkshopFilter> options, string label, ref WorkshopFilter? selected,
        ref string input, string selectionProperty, string inputProperty)
    {
        if (options.Count == 0 || options[0].Value is not null || options[0].Label == label) return;
        var previous = options[0];
        var replacement = new WorkshopFilter(label, null);
        var previousSelection = selected;
        var previousInput = input;
        options[0] = replacement;
        selected = previousSelection is { Value: null } ? replacement : previousSelection;
        input = previousInput == previous.Label ? label : previousInput;
        Changed(selectionProperty);
        Changed(inputProperty);
    }

    public string WorkshopCategoryInput { get => _categoryInput; set => Set(ref _categoryInput, value ?? ""); }
    public string WorkshopVersionInput { get => _versionInput; set => Set(ref _versionInput, value ?? ""); }
    public WorkshopFilter? SelectedWorkshopCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value)) WorkshopCategoryInput = value?.Label ?? ""; }
    }
    public WorkshopFilter? SelectedWorkshopVersion
    {
        get => _selectedVersion;
        set
        {
            if (Set(ref _selectedVersion, value?.Value is { } tag && !VersionTagMatchesTarget(tag) ? WorkshopVersions[0] : value))
                WorkshopVersionInput = _selectedVersion?.Label ?? "";
        }
    }
    public WorkshopSortOption? SelectedWorkshopSort { get => _selectedSort; set => Set(ref _selectedSort, value); }
    public ICommand ResetWorkshopCommand { get; private set; } = null!;
    public ICommand PreviousWorkshopPageCommand { get; private set; } = null!;
    public ICommand NextWorkshopPageCommand { get; private set; } = null!;
    public ICommand GoToWorkshopPageCommand { get; private set; } = null!;
    public ICommand OpenWorkshopItemCommand { get; private set; } = null!;
    public ICommand BackToWorkshopCommand { get; private set; } = null!;
    public ICommand UseInstalledVersionCommand { get; private set; } = null!;
    public ICommand RefreshWorkshopCommand { get; private set; } = null!;
    public Bitmap? WorkshopPreview { get => _workshopPreview; private set { _workshopPreview?.Dispose(); _workshopPreview = value; Changed(); Changed(nameof(HasWorkshopPreview)); } }
    public bool HasWorkshopPreview => WorkshopPreview is not null;
    public bool HasWorkshopResults => _hasBrowsePage && WorkshopResults.Count > 0;
    public bool NoWorkshopResults => _hasBrowsePage && CanLoadWorkshop && WorkshopResults.Count == 0;
    public bool WorkshopNotLoaded => !_hasBrowsePage || !CanLoadWorkshop;
    public string WorkshopResultLabel => _hasBrowsePage ? T("ModsOnThisPageSteamReleases", WorkshopResults.Count, _workshopTotalCount, (_workshopSearchNeedsRefresh ? T("PreviousFilterResults") : "")) : T("BrowseWorkshop");
    public string WorkshopEmptyPageTitle => _workshopSearchNeedsRefresh ? T("TheVersionFilterChangedSearchAgain") : T("NoReleasesToDisplayOnThisPage");
    public string WorkshopPageLabel => T("PageOf", _workshopPage, _workshopTotalPages);
    public string WorkshopPageInput
    {
        get => _workshopPageInput;
        set => Set(ref _workshopPageInput, value);
    }
    public string WorkshopPageSuffix => T("Pages", _workshopTotalPages);
    // 显式暴露可用性，按钮直接绑定，避免只依赖命令的 CanExecuteChanged。
    public bool CanGoPreviousPage => !IsBusy && CanLoadWorkshop && _activeSearch is not null && _workshopPage > 1;
    public bool CanGoNextPage => !IsBusy && CanLoadWorkshop && _activeSearch is not null && _workshopPage < _workshopTotalPages;
    public bool CanGoToWorkshopPage => !IsBusy && CanLoadWorkshop && _activeSearch is not null;
    public string WorkshopSize => WorkshopItem is null ? "" : T("FileSizeMB", WorkshopItem.FileSizeBytes / 1024d / 1024d);
    public Task PreviewLoadingTask { get; private set; } = Task.CompletedTask;

    private void InitializeWorkshopCommands()
    {
        _selectedCategory = WorkshopCategories[0];
        _selectedVersion = WorkshopVersions[0];
        _selectedSort = WorkshopSorts[0];
        ResetWorkshopCommand = new AsyncCommand(ResetWorkshopAsync, () => !IsBusy);
        PreviousWorkshopPageCommand = new AsyncCommand(() => SearchWorkshopAsync(_workshopPage - 1, true), () => !IsBusy && CanLoadWorkshop && _activeSearch is not null && _workshopPage > 1);
        NextWorkshopPageCommand = new AsyncCommand(() => SearchWorkshopAsync(_workshopPage + 1, true), () => !IsBusy && CanLoadWorkshop && _activeSearch is not null && _workshopPage < _workshopTotalPages);
        GoToWorkshopPageCommand = new AsyncCommand(GoToWorkshopPageAsync, () => CanGoToWorkshopPage);
        OpenWorkshopItemCommand = new AsyncCommand(() => RunOperationAsync(T("OpenSteamWorkshop2"), _ =>
        {
            if (WorkshopItem is { } item) _navigator.OpenWorkshop(item.PublishedFileId);
            return Task.CompletedTask;
        }), () => !IsBusy && HasWorkshopItem);
        BackToWorkshopCommand = new RelayCommand(_ => { CancelWorkshopOperations(); CancelDetailPreview(); ClearDetailProject(); WorkshopItem = null; WorkshopPreview = null; SetWorkshopDataStatus(null); }, () => !IsBusy);
        RefreshWorkshopCommand = new AsyncCommand(() => WorkshopItem is { } item
            ? LoadProjectDetailAsync(item.PublishedFileId.ToString(), item.Subscriptions, _detailProject, true)
            : SearchWorkshopAsync(_activeSearch is null ? 1 : _workshopPage, _activeSearch is not null, true),
            () => !IsBusy && CanLoadWorkshop);
        UseInstalledVersionCommand = new RelayCommand(_ => { _targetVersionOverride = null; UpdateWorkshopRecommendations(); }, () => !IsBusy);
    }

    private Task GoToWorkshopPageAsync()
    {
        if (!CanGoToWorkshopPage) return Task.CompletedTask;
        if (!int.TryParse(_workshopPageInput.Trim(), out var requested))
        {
            _workshopPageInput = _workshopPage.ToString();
            Changed(nameof(WorkshopPageInput));
            return Task.CompletedTask;
        }
        var target = Math.Clamp(requested, 1, _workshopTotalPages);
        _workshopPageInput = target.ToString();
        Changed(nameof(WorkshopPageInput));
        return target == _workshopPage ? Task.CompletedTask : SearchWorkshopAsync(target, true);
    }

    private static bool LooksLikeReference(string input) =>
        !string.IsNullOrWhiteSpace(input) && (input.Trim().All(char.IsDigit) || input.Contains("://", StringComparison.Ordinal));

    private void RequestWorkshopPageRefresh()
    {
        _workshopPageRefreshPending = true;
        _ = RefreshWorkshopPageIfNeededAsync();
    }

    private Task RefreshWorkshopPageIfNeededAsync()
    {
        if (!_workshopPageRefreshPending || !IsWorkshop || IsBusy || _disposed || !_localModMonitoringEnabled ||
            !_workshop.CanBrowse || !CanLoadWorkshop) return Task.CompletedTask;
        _workshopPageRefreshPending = false;
        var request = _activeSearch ?? _lastSubmittedWorkshopSearch ?? new WorkshopSearchRequest();
        if (request.VersionTag is { } tag && !VersionTagMatchesTarget(tag)) request = request with { Page = 1, VersionTag = null };
        return SearchWorkshopCoreAsync(request, request.Page, false, true);
    }

    public Task ResetWorkshopAsync()
    {
        WorkshopInput = "";
        SelectedWorkshopCategory = WorkshopCategories[0];
        SelectedWorkshopVersion = WorkshopVersions[0];
        SelectedWorkshopSort = WorkshopSorts[0];
        WorkshopCategoryInput = WorkshopCategories[0].Label;
        WorkshopVersionInput = WorkshopVersions[0].Label;
        return SearchWorkshopAsync();
    }

    public Task SearchWorkshopAsync(int page = 1, bool useActiveQuery = false, bool forceRefresh = false) =>
        SearchWorkshopCoreAsync(null, page, useActiveQuery, forceRefresh);

    private Task SearchWorkshopCoreAsync(WorkshopSearchRequest? submittedRequest, int page, bool useActiveQuery, bool forceRefresh)
    {
        var task = RunWorkshopOperationAsync(T("LoadSteamWorkshopListings"), async (ct, requestId) =>
        {
            RequireWorkshopVersion();
            if (submittedRequest is null && page == 1 && !useActiveQuery) CommitWorkshopFilterInput();
            var request = submittedRequest ?? (page == 1 && !useActiveQuery ? new WorkshopSearchRequest(WorkshopInput.Trim(), SelectedWorkshopSort?.Value ?? WorkshopSort.Trending, 1,
                SelectedWorkshopCategory?.Value, SelectedWorkshopVersion?.Value) :
                (_activeSearch ?? throw new InvalidOperationException(T("SearchTheWorkshopFirst"))) with { Page = page });
            var keepCurrentRows = _hasBrowsePage && _activeSearch == request;
            _workshopPageRefreshPending = false;
            _lastSubmittedWorkshopSearch = request;
            _workshopSearchNeedsRefresh = false;
            RememberWorkshopSelections();
            CancelDetailPreview();
            ClearDetailProject();
            WorkshopItem = null;
            WorkshopPreview = null;
            if (!keepCurrentRows)
            {
                CancelPreviews();
                foreach (var row in WorkshopResults) row.Dispose();
                WorkshopResults.Clear();
                _workshopProjects = [];
                _hasBrowsePage = false;
            }
            _activeSearch = request;
            NotifyBrowseChanged();
            await foreach (var update in _workshop.ObserveSearchAsync(request, forceRefresh, ct))
            {
                if (!IsCurrentWorkshopRequest(requestId, ct)) return;
                if (update.Page is { } result)
                {
                    _workshopProjects = WorkshopCatalog.Group(MergeVerifiedWorkshopMetadata(result.Items));
                    _workshopPage = result.Page;
                    _workshopTotalPages = result.TotalPages;
                    _workshopTotalCount = result.TotalCount;
                    _hasBrowsePage = true;
                    RebuildWorkshopRows();
                    UpdateFilters(WorkshopCategories, result.CategoryTags, SelectedWorkshopCategory?.Value);
                    SelectedWorkshopCategory = WorkshopCategories.FirstOrDefault(f => f.Value == SelectedWorkshopCategory?.Value) ?? WorkshopCategories[0];
                    _workshopVersionTags = result.VersionTags;
                    UpdateWorkshopVersionFilters();
                    NotifyBrowseChanged();
                    WorkshopLoading = false;
                }
                WorkshopRefreshing = update.IsRefreshing;
                if (update.Error is { } error)
                {
                    ErrorMessage = T("CouldNotRefreshWorkshopData", error);
                    SetWorkshopDataStatus(_hasBrowsePage ? "TheRefreshFailedShowingTheLastLoaded" : "WorkshopDataIsUnavailableTryAgainLater");
                }
                else
                {
                    SetWorkshopDataStatus(update.IsStale ? "ShowingTheLastLoadedDataWhileChecking" : update.IsRefreshing ? "CheckingForUpdates" : null);
                    Status = !CanLoadWorkshop ? WorkshopVersionHint : _workshopSearchNeedsRefresh ? T("TheVersionFilterChangedSearchAgain2") :
                        WorkshopResults.Count == 0 ? T("ThisPageHasNoReleasesToDisplay") : T("LoadedSteamWorkshopPageWithCompatibleVersions", resultPageLabel());
                }
                Changed(nameof(HasWorkshopDataStatus));
            }
            string resultPageLabel() => _workshopPage.ToString();
        });
        WorkshopLoadingTask = task;
        return task;
    }

    public Task LoadWorkshopDetailAsync(string reference, long? subscriptions = null) => LoadProjectDetailAsync(reference, subscriptions, null);

    private Task LoadProjectDetailAsync(string reference, long? subscriptions, WorkshopProject? project, bool forceRefresh = false)
    {
        var task = RunWorkshopOperationAsync(T("LoadWorkshopDetails"), async (ct, requestId) =>
        {
            RequireWorkshopVersion();
            var parsed = WorkshopReference.Parse(reference);
            project = _workshopProjects.FirstOrDefault(group => group.Releases.Any(item => item.PublishedFileId == parsed.PublishedFileId)) ?? project;
            var known = project?.Releases.FirstOrDefault(item => item.PublishedFileId == parsed.PublishedFileId);
            if (known is null) known = _workshopProjects.SelectMany(group => group.Releases).FirstOrDefault(item => item.PublishedFileId == parsed.PublishedFileId);
            CancelDetailPreview();
            if (WorkshopItem?.PublishedFileId != parsed.PublishedFileId) WorkshopPreview = null;
            _detailHasFullDescription = false;
            _detailIsFresh = false;
            if (known is not null && CanOpenWorkshopItem(known))
                PresentWorkshopDetail(known, project, false, false);
            else
            {
                ClearDetailProject();
                WorkshopItem = null;
                WorkshopPreview = null;
            }
            await foreach (var update in _workshop.ObserveDetailAsync(reference, known, forceRefresh, ct))
            {
                if (!IsCurrentWorkshopRequest(requestId, ct)) return;
                RequireWorkshopVersion();
                if (update.IsUnavailable)
                {
                    RemoveUnavailableWorkshopItem(parsed.PublishedFileId);
                    ClearDetailProject();
                    WorkshopItem = null;
                    WorkshopPreview = null;
                    throw new InvalidOperationException(update.Error ?? T("ThisWorkshopReleaseIsUnavailable"));
                }
                if (update.Item is { } item)
                {
                    if (update.HasFullDescription && !update.IsStale)
                        RememberVerifiedWorkshopMetadata(item, update.FetchedAt);
                    var latestItem = item with { Subscriptions = subscriptions ?? item.Subscriptions };
                    var currentProject = UpdateCachedWorkshopItem(latestItem, CancellationToken.None) ?? project;
                    if (!CanOpenWorkshopItem(latestItem))
                    {
                        ClearDetailProject();
                        WorkshopItem = null;
                        WorkshopPreview = null;
                        throw new InvalidOperationException(T("ThisReleaseIsIncompatibleOrUnverifiedChoose"));
                    }
                    PresentWorkshopDetail(latestItem, currentProject, update.HasFullDescription, !update.IsStale, update.FetchedAt);
                    WorkshopLoading = false;
                }
                WorkshopRefreshing = update.IsRefreshing;
                if (update.Error is { } error)
                {
                    ErrorMessage = T("CouldNotRefreshWorkshopDetails", error);
                    SetWorkshopDataStatus(HasWorkshopItem ? "TheRefreshFailedShowingSavedDetailsRefresh" : "WorkshopDetailsAreUnavailableTryAgainLater");
                    _detailIsFresh = false;
                    Changed(nameof(CanDownloadWorkshopItem));
                    RefreshCommands();
                }
                else
                {
                    SetWorkshopDataStatus(!update.HasFullDescription ? "ShowingTheSummaryWhileLoadingTheFull" : update.IsStale ? "ShowingTheLastDescriptionWhileCheckingFor" : update.IsRefreshing ? "CheckingForUpdates" : null);
                    Status = update.IsRefreshing ? T("ShowingExistingDataWhileRefreshingWorkshopDetails") : T("WorkshopDetailsLoaded");
                }
                Changed(nameof(HasWorkshopDataStatus));
            }
        });
        WorkshopLoadingTask = task;
        return task;
    }

    private void PresentWorkshopDetail(WorkshopItem item, WorkshopProject? project, bool complete, bool fresh, DateTimeOffset? fetchedAt = null)
    {
        _detailHasFullDescription = complete;
        _detailIsFresh = fresh;
        _detailFetchedAt = fetchedAt;
        WorkshopItem = item;
        var current = project is null ? WorkshopCatalog.Group([item])[0]
            : project with { Releases = project.Releases.Select(release => release.PublishedFileId == item.PublishedFileId ? item : release).ToArray() };
        SetDetailProject(current, item.PublishedFileId);
        Changed(nameof(WorkshopDescriptionLabel));
        Changed(nameof(CanDownloadWorkshopItem));
        RefreshCommands();
        if (_detailPreviewItemId != item.PublishedFileId || _detailPreviewUrl != item.PreviewImageUrl || _detailCancellation is null)
        {
            CancelDetailPreview();
            _detailPreviewItemId = item.PublishedFileId;
            _detailPreviewUrl = item.PreviewImageUrl;
            _detailCancellation = new CancellationTokenSource();
            PreviewLoadingTask = LoadDetailPreviewAsync(item.PreviewImageUrl, _detailCancellation.Token);
        }
    }

    private void RemoveUnavailableWorkshopItem(ulong id)
    {
        _verifiedWorkshopMetadata.Remove(id);
        _workshopProjects = WorkshopCatalog.Group(_workshopProjects.SelectMany(project => project.Releases).Where(item => item.PublishedFileId != id));
        RebuildWorkshopRows();
        NotifyBrowseChanged();
    }

    private bool IsCurrentWorkshopRequest(long requestId, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && requestId == _workshopRequestId;

    private async Task RunWorkshopOperationAsync(string action, Func<CancellationToken, long, Task> work)
    {
        if (IsBusy || _disposed) return;
        _workshopOperation?.Cancel();
        var requestId = ++_workshopRequestId;
        using var operation = new CancellationTokenSource();
        _workshopOperation = operation;
        ErrorMessage = null;
        LastError = null;
        SetWorkshopDataStatus(null);
        WorkshopLoading = true;
        WorkshopRefreshing = false;
        Status = T("InProgress", action);
        try { await work(operation.Token, requestId); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (requestId == _workshopRequestId) Status = T("WorkshopRequestCancelledExistingDataWasKept");
        }
        catch (Exception error)
        {
            if (IsCurrentWorkshopRequest(requestId, operation.Token))
            {
                LastError = error;
                ErrorMessage = T("Failed", action, error.Message);
                Status = T("WorkshopDataCouldNotRefreshSeeThe");
            }
        }
        finally
        {
            if (requestId == _workshopRequestId)
            {
                _workshopOperation = null;
                WorkshopLoading = false;
                WorkshopRefreshing = false;
            }
        }
    }

    private void NotifyWorkshopActivity()
    {
        Changed(nameof(WorkshopWorking));
        Changed(nameof(CanCancelOperation));
        RefreshCommands();
        _ = RefreshWorkshopPageIfNeededAsync();
    }

    private void CancelWorkshopOperations()
    {
        ++_workshopRequestId;
        _workshopOperation?.Cancel();
        _workshopOperation = null;
        WorkshopLoading = false;
        WorkshopRefreshing = false;
    }

    private void SetDetailProject(WorkshopProject project, ulong? selectedId)
    {
        _detailProject = project;
        _settingDetailVersions = true;
        try
        {
            DetailVersions.Clear();
            var ranked = WorkshopCatalog.Rank(project, WorkshopTargetVersion).Where(choice => CanOpenWorkshopItem(choice.Item)).ToArray();
            for (var i = 0; i < ranked.Length; i++) DetailVersions.Add(new WorkshopVersionOption(ranked[i], i == 0 && ranked[i].Compatibility == CompatibilityStatus.DeclaredMatch));
            SelectedDetailVersion = DetailVersions.FirstOrDefault(v => v.Choice.Item.PublishedFileId == selectedId) ?? DetailVersions.FirstOrDefault();
        }
        finally { _settingDetailVersions = false; }
        if (SelectedDetailVersion is { } selected)
            foreach (var row in WorkshopResults.Where(row => row.Project.Key == project.Key))
                row.SelectedRelease = row.Versions.FirstOrDefault(option => option.Choice.Item.PublishedFileId == selected.Choice.Item.PublishedFileId);
        Changed(nameof(WorkshopReleaseAvailability));
        RefreshCommands();
    }

    private void ClearDetailProject()
    {
        _detailProject = null;
        _selectedDetailVersion = null;
        DetailVersions.Clear();
        Changed(nameof(SelectedDetailVersion));
        Changed(nameof(WorkshopRecommendation));
        Changed(nameof(IsWorkshopCompatibilityMatched));
        Changed(nameof(IsWorkshopCompatibilityMismatch));
    }

    private void UpdateWorkshopRecommendations()
    {
        Changed(nameof(WorkshopTargetVersion));
        Changed(nameof(WorkshopRecommendationBasis));
        Changed(nameof(HasWorkshopTargetVersion));
        Changed(nameof(CanLoadWorkshop));
        Changed(nameof(WorkshopLandingText));
        UpdateWorkshopVersionFilters();
        RebuildWorkshopRows();
        if (WorkshopItem is { } selected && !CanOpenWorkshopItem(selected))
        {
            CancelWorkshopOperations();
            CancelDetailPreview();
            ClearDetailProject();
            WorkshopItem = null;
            WorkshopPreview = null;
            Status = HasWorkshopTargetVersion ? T("TheCurrentVersionChangedDetailsWereClosed", Game.ShortName) : WorkshopVersionHint;
        }
        else if (_detailProject is not null) SetDetailProject(_detailProject, WorkshopItem?.PublishedFileId);
        NotifyBrowseChanged();
        RefreshCommands();
    }

    private bool CanOpenWorkshopItem(WorkshopItem item) => item.ConsumerAppId == Game.AppId &&
        (!Game.RequiresDeclaredWorkshopCompatibility || IsDeclaredMatch(item));

    private bool IsDeclaredMatch(WorkshopItem item) => !Game.RequiresDeclaredWorkshopCompatibility ||
        (HasWorkshopTargetVersion &&
         WorkshopCatalog.Rank(WorkshopCatalog.Group([item])[0], WorkshopTargetVersion)[0].Compatibility == CompatibilityStatus.DeclaredMatch);

    private bool VersionTagMatchesTarget(string tag)
    {
        if (!Game.RequiresDeclaredWorkshopCompatibility) return true;
        if (!HasWorkshopTargetVersion) return false;
        var version = Regex.Match(tag, @"^\s*(?<version>[0-9]+\.[0-9]+(?:\.[0-9]+)*)(?=\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return version.Success && CompatibilityEvaluator.Evaluate(version.Groups["version"].Value, WorkshopTargetVersion).Status == CompatibilityStatus.DeclaredMatch;
    }

    private void UpdateWorkshopVersionFilters()
    {
        if (_activeSearch?.VersionTag is { } activeTag && !VersionTagMatchesTarget(activeTag))
        {
            _activeSearch = null;
            _workshopSearchNeedsRefresh = true;
            Status = T("TheVersionFilterChangedSearchAgain2");
        }
        var selected = SelectedWorkshopVersion?.Value;
        if (selected is not null && !VersionTagMatchesTarget(selected)) selected = null;
        UpdateFilters(WorkshopVersions, _workshopVersionTags.Where(VersionTagMatchesTarget).ToArray(), selected);
        SelectedWorkshopVersion = WorkshopVersions.FirstOrDefault(filter => filter.Value == selected) ?? WorkshopVersions[0];
    }

    private void RequireWorkshopVersion()
    {
        if (!CanLoadWorkshop) throw new InvalidOperationException(WorkshopVersionHint);
    }

    private void CommitWorkshopFilterInput()
    {
        static WorkshopFilter Resolve(string input, ObservableCollection<WorkshopFilter> options, WorkshopFilter? selected)
        {
            var text = input.Trim();
            if (text.Length == 0) return options[0];
            if (selected is not null && string.Equals(selected.Label, text, StringComparison.OrdinalIgnoreCase)) return selected;
            return options.FirstOrDefault(option =>
                string.Equals(option.Label, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase)) ?? new WorkshopFilter(text, text);
        }
        var category = Resolve(WorkshopCategoryInput, WorkshopCategories, SelectedWorkshopCategory);
        var version = Resolve(WorkshopVersionInput, WorkshopVersions, SelectedWorkshopVersion);
        if (version.Value is { } tag && !VersionTagMatchesTarget(tag))
            throw new InvalidOperationException(T("VersionFilterDoesNotMatchTargetChange", tag, WorkshopTargetVersion));
        SelectedWorkshopCategory = category;
        SelectedWorkshopVersion = version;
    }

    private void RememberWorkshopSelections()
    {
        foreach (var row in WorkshopResults)
            if (row.SelectedRelease is { } selected) _workshopSelections[row.Project.Key] = selected.Choice.Item.PublishedFileId;
    }

    private WorkshopProject? UpdateCachedWorkshopItem(WorkshopItem item, CancellationToken cancellationToken)
    {
        if (!_workshopProjects.Any(project => project.Releases.Any(release => release.PublishedFileId == item.PublishedFileId))) return null;
        _workshopProjects = WorkshopCatalog.Group(_workshopProjects.SelectMany(project => project.Releases)
            .Select(release => release.PublishedFileId == item.PublishedFileId ? item : release));
        RebuildWorkshopRows(cancellationToken);
        NotifyBrowseChanged();
        return _workshopProjects.First(project => project.Releases.Any(release => release.PublishedFileId == item.PublishedFileId));
    }

    private void RememberVerifiedWorkshopMetadata(WorkshopItem item, DateTimeOffset? fetchedAt = null)
    {
        RemoveExpiredWorkshopMetadata();
        if (_verifiedWorkshopMetadata.TryGetValue(item.PublishedFileId, out var previous) && previous.Revision > item.UpdatedAt) return;
        // 列表只保留已打开详情的兼容声明，正文与其他详情仍由详情缓存负责。
        _verifiedWorkshopMetadata[item.PublishedFileId] = new(item.UpdatedAt, item.Tags, fetchedAt ?? _workshopMetadataTimeProvider.GetUtcNow());
        if (_verifiedWorkshopMetadata.Count > 128)
        {
            var oldest = _verifiedWorkshopMetadata.OrderBy(pair => pair.Value.CheckedAt).ThenBy(pair => pair.Key).First().Key;
            _verifiedWorkshopMetadata.Remove(oldest);
        }
    }

    private IReadOnlyList<WorkshopItem> MergeVerifiedWorkshopMetadata(IReadOnlyList<WorkshopItem> items)
    {
        RemoveExpiredWorkshopMetadata();
        return items.Select(item => _verifiedWorkshopMetadata.TryGetValue(item.PublishedFileId, out var verified) && verified.Revision >= item.UpdatedAt
            ? item with { Tags = verified.Tags }
            : item).ToArray();
    }

    private void RemoveExpiredWorkshopMetadata()
    {
        var now = _workshopMetadataTimeProvider.GetUtcNow();
        foreach (var id in _verifiedWorkshopMetadata.Where(pair => !WorkshopCachePolicy.CanDisplay(pair.Value.CheckedAt, now)).Select(pair => pair.Key).ToArray())
            _verifiedWorkshopMetadata.Remove(id);
    }

    private void RebuildWorkshopRows(CancellationToken cancellationToken = default)
    {
        RememberWorkshopSelections();
        CancelPreviews();
        var existing = WorkshopResults.ToDictionary(row => row.Project.Key, StringComparer.Ordinal);
        var visible = new List<WorkshopModRow>();
        if (CanLoadWorkshop)
        {
            // 声明匹配的排在前面，不匹配的仍然列出：工坊作者常常不填版本标签，严格过滤会让热门页几乎为空。
            var ordered = _workshopProjects
                .Select(project => (Project: project, Matched: WorkshopCatalog.Rank(project, WorkshopTargetVersion)
                    .Any(choice => choice.Compatibility == CompatibilityStatus.DeclaredMatch)))
                .OrderByDescending(entry => entry.Matched)
                .Take(WorkshopPageItemLimit);
            foreach (var (project, _) in ordered)
            {
                var row = existing.GetValueOrDefault(project.Key);
                if (row is null)
                    row = new WorkshopModRow(project, WorkshopTargetVersion, _workshop,
                        item => LoadProjectDetailAsync(item.PublishedFileId.ToString(), item.Subscriptions,
                            _workshopProjects.FirstOrDefault(current => current.Releases.Any(release => release.PublishedFileId == item.PublishedFileId))));
                else row.UpdateProject(project, WorkshopTargetVersion);
                if (_workshopSelections.TryGetValue(project.Key, out var selectedId) &&
                    row.Versions.FirstOrDefault(option => option.Choice.Item.PublishedFileId == selectedId) is { } selection)
                    row.SelectedRelease = selection;
                visible.Add(row);
            }
        }
        foreach (var row in existing.Values)
            if (!visible.Contains(row)) row.Dispose();
        WorkshopResults.Clear();
        foreach (var row in visible) WorkshopResults.Add(row);
        _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PreviewLoadingTask = Task.WhenAll(visible.Select(row => row.LoadPreviewAsync(_previewCancellation.Token)));
    }

    private async Task LoadDetailPreviewAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var expected = WorkshopItem?.PublishedFileId;
        var operation = _detailCancellation;
        try
        {
            await foreach (var update in _workshop.ObservePreviewAsync(url, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested || _disposed || !ReferenceEquals(operation, _detailCancellation) ||
                    WorkshopItem?.PublishedFileId != expected || WorkshopItem?.PreviewImageUrl != url) return;
                using var stream = update.OpenRead();
                if (stream is null) continue;
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                if (WorkshopPreview is not null && _detailPreviewDigest == digest) continue;
                stream.Position = 0;
                var bitmap = Bitmap.DecodeToWidth(stream, 900);
                if (cancellationToken.IsCancellationRequested || _disposed || !ReferenceEquals(operation, _detailCancellation) ||
                    WorkshopItem?.PublishedFileId != expected || WorkshopItem?.PreviewImageUrl != url) bitmap.Dispose();
                else { WorkshopPreview = bitmap; _detailPreviewDigest = digest; }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Keep text and an existing image if refresh fails. */ }
    }

    private static void UpdateFilters(ObservableCollection<WorkshopFilter> target, IReadOnlyList<string> tags, string? selected)
    {
        var first = target[0];
        target.Clear();
        target.Add(first);
        foreach (var tag in tags) target.Add(new WorkshopFilter(tag, tag));
        if (selected is not null && !tags.Contains(selected)) target.Add(new WorkshopFilter(selected, selected));
    }

    private void NotifyBrowseChanged()
    {
        var pageText = _workshopPage.ToString();
        if (_workshopPageInput != pageText) { _workshopPageInput = pageText; Changed(nameof(WorkshopPageInput)); }
        foreach (var property in new[] { nameof(HasWorkshopResults), nameof(NoWorkshopResults), nameof(WorkshopNotLoaded), nameof(WorkshopResultLabel), nameof(WorkshopPageLabel), nameof(WorkshopPageSuffix), nameof(CanGoPreviousPage), nameof(CanGoNextPage), nameof(CanGoToWorkshopPage), nameof(WorkshopEmptyPageTitle) }) Changed(property);
        RefreshWorkshopCommands();
    }

    private void RefreshWorkshopCommands()
    {
        foreach (var command in new[] { ResetWorkshopCommand, PreviousWorkshopPageCommand, NextWorkshopPageCommand, GoToWorkshopPageCommand, BackToWorkshopCommand, UseInstalledVersionCommand, RefreshWorkshopCommand, OpenWorkshopItemCommand })
        {
            if (command is AsyncCommand asyncCommand) asyncCommand.Refresh();
            if (command is RelayCommand relay) relay.Refresh();
        }
    }

    private void CancelPreviews() { _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null; }
    private void CancelDetailPreview() { _detailCancellation?.Cancel(); _detailCancellation?.Dispose(); _detailCancellation = null; _detailPreviewUrl = null; _detailPreviewItemId = null; }
    private void DisposeWorkshop() { CancelWorkshopOperations(); CancelPreviews(); CancelDetailPreview(); foreach (var row in WorkshopResults) row.Dispose(); WorkshopPreview = null; }
}
