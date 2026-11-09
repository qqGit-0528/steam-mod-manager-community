using static Ck3ModManager.Core.AppText;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    internal void RefreshLocalizedText() => Changed(string.Empty);
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }
}

public sealed class InstalledModRow(InstalledMod mod, string? actualVersion, Action<InstalledModRow, bool>? toggle = null,
    DuplicateModCopies? copies = null) : ObservableObject
{
    private bool _enabled;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Changed(nameof(EnableText)); toggle?.Invoke(this, value); } } }
    public string EnableText => Enabled ? T("Enabled") : T("Disabled");
    public bool CanToggle => Mod.IsAvailable;
    internal void SyncEnabled(bool value) { if (Set(ref _enabled, value, nameof(Enabled))) Changed(nameof(EnableText)); }
    private CompatibilityStatus _compatibility = CompatibilityEvaluator.Evaluate(mod.SupportedGameVersion, actualVersion).Status;
    public InstalledMod Mod { get; } = mod;
    internal bool NeedsWorkshopCompatibility => Mod.WorkshopId is not null && Mod.Management == ModInstallManagement.Steam &&
        _compatibility == CompatibilityStatus.Unknown;
    internal void UpdateCompatibility(CompatibilityStatus status)
    {
        if (_compatibility == status) return;
        _compatibility = status;
        Changed(nameof(Compatibility));
        Changed(nameof(IsCompatibilityMatched));
        Changed(nameof(IsCompatibilityMismatch));
    }
    public string Name => Mod.Name;
    public string Detail => InstallationLabel(Mod);
    public bool HasDuplicateCopies => copies is not null;
    public string DuplicateLabel => copies is { Confirmed: true } ? T("WorkshopCopies") : T("PossibleDuplicate");
    public string? DuplicateDetails => copies is null ? null : string.Join(Environment.NewLine,
        new[] { Mod }.Concat(copies.OtherCopies).Select(copy => $"{copy.Name} · {InstallationLabel(copy)} · {copy.ContentPath}"));
    public string PickerName => HasDuplicateCopies ? $"{Name} · {Detail}" : Name;

    private static string InstallationLabel(InstalledMod installation) =>
        $"{ManagementLabel(installation.Management)}  ·  Mod {installation.ModVersion ?? T("VersionUnknown")}";

    internal static string ManagementLabel(ModInstallManagement management) => management switch
    {
        ModInstallManagement.Steam => "STEAM",
        ModInstallManagement.Application => T("AppManaged"),
        ModInstallManagement.Local => T("Local"),
        _ => T("ExternalPath")
    };
    public string SupportedVersion => Mod.SupportedGameVersion ?? T("GameVersionUndeclared");
    public string ContentPath => Mod.ContentPath;
    public string Availability => Mod.IsAvailable ? T("FilesAvailable") : T("FilesMissing");
    internal CompatibilityStatus ResolvedCompatibility => _compatibility;
    public string Compatibility => CompatibilityDisplay.Label(_compatibility);
    public bool IsCompatibilityMatched => _compatibility == CompatibilityStatus.DeclaredMatch;
    public bool IsCompatibilityMismatch => _compatibility == CompatibilityStatus.DeclaredMismatch;
    public string Dependencies => Mod.Dependencies.Count == 0 ? T("NoDependenciesDeclaredInTheDescriptor") : T("DeclaredDependencies", string.Join(T("ListSeparator"), Mod.Dependencies));
    public override string ToString() => Name;
}

public sealed class ProfileModRow(ProfileMod mod, int order, string? actualVersion = null, Action? changed = null) : ObservableObject
{
    private ProfileMod _mod = mod;
    private readonly CompatibilityStatus _compatibility = CompatibilityEvaluator.Evaluate(mod.SupportedGameVersion, actualVersion).Status;
    public ProfileMod Mod => _mod;
    public bool Enabled
    {
        get => _mod.Enabled;
        set { if (_mod.Enabled == value) return; _mod = _mod with { Enabled = value }; Changed(); Changed(nameof(EnableText)); changed?.Invoke(); }
    }
    public string EnableText => Enabled ? T("Enabled") : T("Disabled");
    private int _order = order;
    public int Order { get => _order; set => Set(ref _order, value); }
    public string Name => Mod.DisplayName;
    public string Detail => PortableProfileService.IsUnresolved(Mod) ?
        T("LocalFilesPendingRequestedVersion", Mod.ModVersion ?? T("Undeclared")) : T("ModGame", Mod.ModVersion ?? T("VersionUnknown"), Mod.SupportedGameVersion ?? T("Unknown"), Mod.ContentPath);
    public string Compatibility => CompatibilityDisplay.Label(_compatibility);
    public bool IsCompatibilityMatched => _compatibility == CompatibilityStatus.DeclaredMatch;
    public bool IsCompatibilityMismatch => _compatibility == CompatibilityStatus.DeclaredMismatch;
    public bool IsCompatibilityUnknown => _compatibility == CompatibilityStatus.Unknown;
}

/// <summary>Desktop state and commands. All disk and network access stays behind application services.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceService _workspace;
    private readonly WorkshopService _workshop;
    private readonly IExternalNavigator _navigator;
    private readonly ILocalModMonitor? _localModMonitor;
    private CancellationTokenSource? _localModMonitoring;
    private AppSettings? _localModSettings;
    private bool _localModMonitoringEnabled = true;
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private string _section = "overview";
    private bool _isBusy;
    private string _status = T("PreparingToDiscoverLocalContent");
    private string? _errorMessage;
    private string? _gameVersion;
    private string? _gameDirectory;
    private string _userDirectory = "";
    private DateTimeOffset? _lastScan;
    private GameEnvironment? _localEnvironment;
    private string _localSearch = "";
    private int _localPage = 1;
    private string _workshopInput = "";
    private WorkshopItem? _workshopItem;
    private string _steamPath = "";
    private string _gamePath = "";
    private string _userPath = "";
    private ModProfile? _selectedProfile;
    private ProfileModRow? _selectedProfileMod;
    private InstalledModRow? _selectedInstalledMod;
    private string _profileName = "";
    private string _profileGameVersion = "";
    private bool _isProfileDirty;
    private Guid? _pendingDeleteProfileId;
    private readonly Dictionary<Guid, ModProfile> _drafts = [];
    private CancellationTokenSource? _compatibilityLookup;

    public MainWindowViewModel(WorkspaceService workspace, WorkshopService workshop, IExternalNavigator navigator,
        TimeProvider? timeProvider = null, ILocalModMonitor? localModMonitor = null, WorkshopDownloadService? downloads = null,
        IWorkshopDependencySource? dependencySource = null, IUiLanguagePreference? uiLanguage = null)
    {
        _workspace = workspace;
        _workshop = workshop;
        _navigator = navigator;
        _localModMonitor = localModMonitor;
        _workshopMetadataTimeProvider = timeProvider ?? TimeProvider.System;
        OpenDiscoveredWorkshopCommand = new AsyncCommand(() => RunOperationAsync(T("OpenSteamWorkshop"), _ =>
        {
            _navigator.OpenWorkshop();
            return Task.CompletedTask;
        }));
        NavigateCommand = new RelayCommand(p => Section = p?.ToString() ?? "overview");
        PreviousLocalPageCommand = new RelayCommand(_ => ChangeLocalPage(-1), () => CanGoPreviousLocalPage);
        NextLocalPageCommand = new RelayCommand(_ => ChangeLocalPage(1), () => CanGoNextLocalPage);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        LookupWorkshopCommand = new AsyncCommand(LookupWorkshopAsync, () => !IsBusy && CanLoadWorkshop);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync, () => !IsBusy);
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync, () => !IsBusy && HasProfile);
        CancelCommand = new RelayCommand(_ => CancelCurrentOperation(), () => CanCancelOperation);
        NewProfileCommand = new RelayCommand(_ => CreateProfile(), () => !IsBusy);
        DeleteProfileCommand = new RelayCommand(
            p => _ = DeleteProfileAsync(p as ModProfile), () => !IsBusy && HasProfile);
        AddModCommand = new RelayCommand(_ => AddSelectedMod(), () => !IsBusy && HasProfile && SelectedInstalledMod is not null);
        RemoveModCommand = new RelayCommand(_ => RemoveSelectedMod(), () => !IsBusy && SelectedProfileMod is not null);
        MoveUpCommand = new RelayCommand(_ => MoveSelectedMod(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelectedMod(1), () => CanMove(1));
        InitializeDownloads(downloads);
        InitializeInstalledModRemoval();
        InitializeGameActivation();
        InitializeProfileTransfer();
        InitializePlaysetSnapshots();
        InitializeSaveBindings();
        InitializeWorkshopUpdates();
        InitializeFileConflicts();
        InitializeDependencies(dependencySource);
        InitializePortableModTransfers();
        InitializeDiagnostics();
        LaunchGameCommand = new AsyncCommand(LaunchGameWithProfileAsync, () => !IsBusy && !_disposed);
        InitializeWorkshopCommands();
        InitializeUiLanguage(uiLanguage);
    }

    public ObservableCollection<InstalledModRow> InstalledMods { get; } = [];
    public ObservableCollection<ModProfile> Profiles { get; } = [];
    public ObservableCollection<ProfileModRow> ProfileMods { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ICommand OpenDiscoveredWorkshopCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand PreviousLocalPageCommand { get; }
    public ICommand NextLocalPageCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LookupWorkshopCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand AddModCommand { get; }
    public ICommand RemoveModCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public Exception? LastError { get; private set; }

    public GameDefinition Game => _workshop.Game;
    private Func<string?, bool, CancellationToken, Task>? _refreshGameCatalog;
    public IReadOnlyList<GameDefinition> Games { get; private set; } = GameCatalog.All;

    internal void AttachGameCatalog(IReadOnlyList<GameDefinition> games, Func<string?, bool, CancellationToken, Task> refresh)
    {
        Games = games;
        _refreshGameCatalog = refresh;
    }

    internal Task RefreshGameCatalogAsync(CancellationToken token = default, bool replaceSteamDirectory = false) =>
        _refreshGameCatalog?.Invoke(_localModSettings?.SteamDirectory, replaceSteamDirectory, token) ?? Task.CompletedTask;

    internal void UpdateGameSelection()
    {
        Changed(nameof(SelectedGame));
        Changed(nameof(CanOpenDiscoveredWorkshop));
    }

    public bool CanOpenDiscoveredWorkshop => Game.IsDiscovered && SelectedGame.HasSteamWorkshop;
    public event Action<GameDefinition>? GameChangeRequested;
    public GameDefinition SelectedGame
    {
        get => Games.FirstOrDefault(game => game.Id == Game.Id) ?? Game;
        set
        {
            if (value is null || value.Id == Game.Id || _disposed) return;
            if (Games.FirstOrDefault(game => game.Id == value.Id) is { } selected) GameChangeRequested?.Invoke(selected);
        }
    }
    public string GameDisplayName => UiLocale.GameName(Game);
    public string GameShortName => Game.ShortName;
    public string WindowTitle => $"Steam Mod Manager · {GameDisplayName}";
    public string LocalLibraryTitle => T("LocalMods3", GameDisplayName);
    public string GameDirectoryLabel => T("GameFolder", Game.ShortName);
    public string UserDataDirectoryLabel => Game.IsDiscovered ? T("LocalModFolder") : T("UserDataFolder", Game.ShortName);
    public string GameDirectoryPlaceholder => T("SelectTheInstallFolder", Game.GameName);
    public string UserDataDirectoryPlaceholder => Game.IsDiscovered ? T("DetectAutomaticallyOrEnterTheFullMod") :
        Game.UserDataDirectory ?? $"Paradox Interactive/{Game.GameName}";
    public string LaunchGameTooltip => SupportsGameActivation && HasProfile && !GameProfileApplied
        ? T("ApplyTheProfileAndLaunchViaSteam", GameDisplayName) : T("LaunchViaSteam", GameDisplayName, Game.ShortName);
    public string WorkshopTargetVersionLabel => T("CurrentVersion", Game.ShortName);

    public string Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            InvalidatePortableTransfers();
            if (!IsDiagnostics) InvalidateDiagnostics();
            if (!IsOverview) CancelInstalledModRemoval();
            if (!IsWorkshop) { _workshopPageRefreshPending = false; CancelWorkshopOperations(); }
            if (value != "profiles")
            {
                ClearPendingDeleteProfile();
                InvalidateProfileChecks();
                if (HasProfileImportPreview) SetImportPreview(null);
            }
            foreach (var p in new[] { nameof(IsOverview), nameof(IsWorkshop), nameof(IsProfiles), nameof(IsSettings), nameof(IsDownloads), nameof(IsDiagnostics), nameof(SectionTitle), nameof(SectionSubtitle) }) Changed(p);
            RefreshInstalledModRemovalCommands();
            ((AsyncCommand)CheckFileConflictsCommand).Refresh();
            RefreshDependencyCommands();
            RefreshPortableTransferCommands();
            RefreshDiagnosticCommands();
            if (IsWorkshop) RequestWorkshopPageRefresh();
        }
    }
    public bool IsOverview => Section == "overview";
    public bool IsWorkshop => Section == "workshop";
    public bool IsProfiles => Section == "profiles";
    public bool IsSettings => Section == "settings";
    public string SectionTitle => Section switch { "downloads" => T("Downloads2", GameDisplayName), "workshop" => T("Workshop3", GameDisplayName), "profiles" => T("Profiles3", GameDisplayName), "settings" => T("Settings2", GameDisplayName), "diagnostics" => T("CK3CrashesAndLogs"), _ => LocalLibraryTitle };
    public string SectionSubtitle => Section switch
    {
        "downloads" => T("SteamManagesInstallationAndDownloadProgress"),
        "workshop" => T("SearchAndDownloadMods"),
        "profiles" => T("ManageAnOrderedModListForEach"),
        "settings" => T("DetectPathsAutomaticallyOrSetYourOwn"),
        "diagnostics" => T("ReadRecentSessionLogsAndPastCrash"),
        _ => T("ManageInstalledModsAndGameVersions")
    };
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Changed(nameof(CanCancelOperation)); RefreshCommands(); } } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string? ErrorMessage { get => _errorMessage; private set { if (Set(ref _errorMessage, value)) Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string GameVersionText => _gameVersion ?? T("Undetected");
    public string GameDirectoryText => _gameDirectory ?? T("GameFolderNotFoundCheckSettings");
    public string UserDirectoryText => _userDirectory.Length == 0 ? T("NotScannedYet") : _userDirectory;
    public string LocalModDirectoriesText { get; private set; } = "";
    public string WorkshopDirectoriesText { get; private set; } = "";
    public string ModCountText => InstalledMods.Count.ToString();
    public string ProfileCountText => Profiles.Count.ToString();
    public string LastScan => _lastScan is { } scan ? T("ScannedAt", scan) : T("NotScannedYet");
    public bool HasWarnings => Warnings.Count > 0;
    public bool IsLibraryEmpty => InstalledMods.Count == 0;
    public bool NoSearchResults => InstalledMods.Count > 0 && MatchedModCount == 0;
    // 本地列表按搜索条件过滤后再分页，页码越界时自动收敛到最后一页。
    private const int LocalPageSize = 10;
    private IEnumerable<InstalledModRow> MatchedMods => InstalledMods.Where(m =>
        string.IsNullOrWhiteSpace(LocalSearch) || m.Name.Contains(LocalSearch, StringComparison.OrdinalIgnoreCase));
    private int MatchedModCount => MatchedMods.Count();
    public int LocalTotalPages => Math.Max(1, (MatchedModCount + LocalPageSize - 1) / LocalPageSize);
    private int EffectiveLocalPage => Math.Min(Math.Max(_localPage, 1), LocalTotalPages);
    public IEnumerable<InstalledModRow> FilteredMods =>
        MatchedMods.Skip((EffectiveLocalPage - 1) * LocalPageSize).Take(LocalPageSize);
    public string LocalPageLabel => T("PageOf", EffectiveLocalPage, LocalTotalPages);
    public bool CanGoPreviousLocalPage => EffectiveLocalPage > 1;
    public bool CanGoNextLocalPage => EffectiveLocalPage < LocalTotalPages;
    public string LocalSearch
    {
        get => _localSearch;
        set { if (Set(ref _localSearch, value)) { _localPage = 1; NotifyLocalPaging(); } }
    }
    private void NotifyLocalPaging()
    {
        foreach (var property in new[] { nameof(FilteredMods), nameof(NoSearchResults), nameof(LocalPageLabel), nameof(CanGoPreviousLocalPage), nameof(CanGoNextLocalPage) })
            Changed(property);
        // 按钮的启用状态由命令的 CanExecute 决定，页码或数量变化时必须同步刷新。
        (PreviousLocalPageCommand as RelayCommand)?.Refresh();
        (NextLocalPageCommand as RelayCommand)?.Refresh();
    }
    private void ChangeLocalPage(int delta)
    {
        var target = EffectiveLocalPage + delta;
        if (target < 1 || target > LocalTotalPages) return;
        _localPage = target;
        NotifyLocalPaging();
    }
    public string WorkshopInput { get => _workshopInput; set { if (Set(ref _workshopInput, value)) RefreshCommands(); } }
    public WorkshopItem? WorkshopItem
    {
        get => _workshopItem;
        private set
        {
            if (!Set(ref _workshopItem, value)) return;
            foreach (var p in new[] { nameof(HasWorkshopItem), nameof(WorkshopEmpty), nameof(WorkshopTitle), nameof(WorkshopDescription), nameof(WorkshopUpdated), nameof(WorkshopTags), nameof(WorkshopDependencies), nameof(WorkshopIdentifier), nameof(WorkshopSize) }) Changed(p);
            Changed(nameof(CanDownloadWorkshopItem));
            RefreshCommands();
        }
    }
    public bool HasWorkshopItem => WorkshopItem is not null;
    public bool WorkshopEmpty => WorkshopItem is null;
    public string WorkshopTitle => WorkshopItem?.Title ?? "";
    public string WorkshopDescription => WorkshopText.Plain(WorkshopItem?.Description ?? "");
    public string WorkshopUpdated => WorkshopItem is null ? "" : T("Updated", WorkshopItem.UpdatedAt.ToLocalTime());
    public string WorkshopIdentifier => WorkshopItem is null ? "" : $"WORKSHOP / {WorkshopItem.PublishedFileId}";
    public string WorkshopTags => WorkshopItem is null ? "" : string.Join("  /  ", WorkshopItem.Tags);
    public string WorkshopDependencies => WorkshopItem?.RequiredItemIds is null
        ? T("DependenciesAreUnconfirmedCheckTheAuthorS")
        : WorkshopItem.RequiredItemIds.Count == 0 ? T("NoDependenciesAreListedInTheWorkshop") : T("WorkshopDependencies", string.Join(T("ListSeparator"), WorkshopItem.RequiredItemIds));

    public string SteamPath { get => _steamPath; set => Set(ref _steamPath, value); }
    public string GamePath { get => _gamePath; set => Set(ref _gamePath, value); }
    public string UserPath { get => _userPath; set => Set(ref _userPath, value); }
    public ModProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            CaptureCurrentDraft();
            if (!Set(ref _selectedProfile, value)) return;
            ClearSaveCheck();
            SetWorkshopUpdateReport(null);
            InvalidateProfileChecks();
            ClearPendingDeleteProfile();
            var editable = value is not null && _drafts.TryGetValue(value.Id, out var draft) ? draft : value;
            _profileName = editable?.Name ?? "";
            _profileGameVersion = editable?.GameVersion ?? "";
            Changed(nameof(ProfileName));
            Changed(nameof(ProfileGameVersion));
            ProfileMods.Clear();
            if (editable is not null) foreach (var mod in editable.Mods) ProfileMods.Add(CreateProfileRow(mod));
            SelectedProfileMod = null;
            if (ProfileMods.Any(row => PortableProfileService.IsUnresolved(row.Mod)))
                ResolveImportedProfileRows(CurrentInstalledMods());
            IsProfileDirty = value is not null && _drafts.ContainsKey(value.Id);
            Changed(nameof(HasProfile));
            Changed(nameof(NoProfile));
            Changed(nameof(ProfileEmpty));
            GameProfileApplied = false;
            SyncLocalModToggles();
            RefreshCommands();
        }
    }
    public InstalledModRow? SelectedInstalledMod
    {
        get => _selectedInstalledMod;
        set
        {
            if (!Set(ref _selectedInstalledMod, value)) return;
            CancelInstalledModRemoval();
            RefreshCommands();
        }
    }
    public ProfileModRow? SelectedProfileMod { get => _selectedProfileMod; set { if (Set(ref _selectedProfileMod, value)) RefreshCommands(); } }
    public string ProfileName { get => _profileName; set { if (Set(ref _profileName, value)) { IsProfileDirty = true; GameProfileApplied = false; Changed(nameof(ActiveProfileLabel)); CaptureCurrentDraft(); } } }
    public string ProfileGameVersion { get => _profileGameVersion; set { if (Set(ref _profileGameVersion, value)) { IsProfileDirty = true; GameProfileApplied = false; CaptureCurrentDraft(); } } }
    public bool IsProfileDirty { get => _isProfileDirty; private set { if (Set(ref _isProfileDirty, value)) Changed(nameof(ProfileSaveState)); } }
    public string ProfileSaveState => IsProfileDirty ? T("UnsavedChanges") : T("ProfileLoaded");
    public string DeleteProfileLabel => _pendingDeleteProfileId is not null ? T("ClickAgainToDelete") : T("DeleteSelectedProfile");
    public bool HasProfile => SelectedProfile is not null;
    public bool NoProfile => !HasProfile;
    public bool ProfileEmpty => HasProfile && ProfileMods.Count == 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(T("InitializeWorkspace"), async ct =>
        {
            ApplyWorkspace(await _workspace.InitializeAsync(ct));
            Status = T("LocalEnvironmentLoaded");
            await InitializeGameProfileAsync(ct);
            await LoadPlaysetSnapshotsAsync(ct);
            await LoadDownloadHistoryAsync(ct);
        }, cancellationToken);
    }

    public Task RefreshAsync() => RunOperationAsync(T("ScanLocalMods"), async ct =>
    {
        ApplyWorkspace(await _workspace.RefreshAsync(CurrentSettings(), ct));
        await RefreshGameCatalogAsync(ct);
        Status = T("ScanCompleteModsFound", InstalledMods.Count);
    });

    public Task LookupWorkshopAsync() => LooksLikeReference(WorkshopInput)
        ? LoadWorkshopDetailAsync(WorkshopInput.Trim()) : SearchWorkshopAsync();

    public Task SaveSettingsAsync() => RunOperationAsync(T("SavePathsAndScan"), async ct =>
    {
        var settings = CurrentSettings();
        var steamDirectoryChanged = _localModSettings?.SteamDirectory != settings.SteamDirectory;
        await _workspace.SaveSettingsAsync(settings, ct);
        ApplyWorkspace(await _workspace.RefreshAsync(settings, ct));
        await RefreshGameCatalogAsync(ct, replaceSteamDirectory: steamDirectoryChanged);
        Status = T("PathsSavedAndLocalEnvironmentRefreshed");
    });

    public Task SaveProfileAsync() => RunOperationAsync(T("SaveRunProfile"), async ct =>
    {
        if (SelectedProfile is null) throw new InvalidOperationException(T("CreateOrSelectAProfileFirst"));
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new InvalidOperationException(T("EnterAProfileName"));
        var profile = new ModProfile(SelectedProfile.Id, ProfileName.Trim(), Clean(ProfileGameVersion),
            ProfileMods.Select(m => m.Mod).ToArray(), DateTimeOffset.UtcNow);
        await _workspace.SaveProfileAsync(profile, ct);
        _drafts.Remove(profile.Id);
        IsProfileDirty = false;
        var index = Profiles.ToList().FindIndex(p => p.Id == profile.Id);
        if (index >= 0) Profiles[index] = profile;
        else Profiles.Add(profile);
        SelectedProfile = profile;
        IsProfileDirty = false;
        Changed(nameof(ProfileCountText));
        Status = T("ProfileSaved");
    });

    public void CreateProfile()
    {
        if (IsBusy) return;
        ClearPendingDeleteProfile();
        var profile = new ModProfile(Guid.NewGuid(), T("NewRunProfile"), _gameVersion, [], DateTimeOffset.UtcNow);
        Profiles.Add(profile);
        SelectedProfile = profile;
        IsProfileDirty = true;
        CaptureCurrentDraft();
        Section = "profiles";
        Changed(nameof(ProfileCountText));
        Status = T("NewProfileIsUnsavedAddModsFrom");
    }

    public async Task DeleteProfileAsync(ModProfile? target = null)
    {
        var profile = target ?? SelectedProfile;
        if (IsBusy || profile is null) return;
        if (_pendingDeleteProfileId != profile.Id)
        {
            // 选中切换会清除确认状态，所以先选中目标配置再记录待确认。
            if (SelectedProfile?.Id != profile.Id) SelectedProfile = profile;
            _pendingDeleteProfileId = profile.Id;
            Changed(nameof(DeleteProfileLabel));
            Status = T("ClickTheDeleteButtonForAgainTo", profile.Name);
            return;
        }

        _pendingDeleteProfileId = null;
        Changed(nameof(DeleteProfileLabel));
        await RunOperationAsync(T("DeleteRunProfile"), async ct =>
        {
            await _workspace.DeleteProfileAsync(profile.Id, ct);
            _drafts.Remove(profile.Id);
            // 先解除脏标记，避免选择切换时把要删除的草稿重新写回。
            IsProfileDirty = false;
            var index = Profiles.IndexOf(profile);
            Profiles.Remove(profile);
            SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Max(0, Math.Min(index, Profiles.Count - 1))];
            Changed(nameof(ProfileCountText));
            Status = T("DeletedProfile", profile.Name);
        });
    }

    private void ClearPendingDeleteProfile()
    {
        if (_pendingDeleteProfileId is null) return;
        _pendingDeleteProfileId = null;
        Changed(nameof(DeleteProfileLabel));
    }

    public void AddSelectedMod()
    {
        if (IsBusy || !HasProfile || SelectedInstalledMod is null) return;
        var mod = SelectedInstalledMod.Mod;
        if (ProfileMods.Any(m => m.Mod.InstalledModId == mod.Id)) { Status = T("ThisModIsAlreadyInTheProfile"); return; }
        if (mod.WorkshopId is { } workshopId && ProfileMods.Any(m => m.Mod.WorkshopId == workshopId))
        { ErrorMessage = T("ThisWorkshopModIsAlreadyInThe"); return; }
        if (!mod.IsAvailable) { ErrorMessage = T("TheModFilesAreMissingCheckThe"); return; }
        ProfileMods.Add(CreateProfileRow(new ProfileMod(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, mod.SupportedGameVersion, mod.ModVersion)));
        SelectedProfileMod = ProfileMods[^1];
        MarkProfileChanged();
    }

    public void RemoveSelectedMod()
    {
        if (IsBusy || SelectedProfileMod is null) return;
        ProfileMods.Remove(SelectedProfileMod);
        SelectedProfileMod = null;
        MarkProfileChanged();
    }

    public void MoveSelectedMod(int offset)
    {
        if (!CanMove(offset)) return;
        var selected = SelectedProfileMod!;
        var index = ProfileMods.IndexOf(selected);
        MoveProfileRow(selected, index, index + offset);
    }

    internal bool CanDragProfileRow(ProfileModRow row) =>
        !_disposed && IsProfiles && !IsBusy && !HasProfileTransferPreview && ProfileMods.Contains(row);

    internal bool DropProfileRow(ProfileModRow row, ProfileModRow target, bool after)
    {
        if (!CanDragProfileRow(row)) return false;
        var from = ProfileMods.IndexOf(row);
        var targetIndex = ProfileMods.IndexOf(target);
        if (targetIndex < 0) return false;

        var insertion = targetIndex + (after ? 1 : 0);
        var destination = insertion > from ? insertion - 1 : insertion;
        if (destination == from) return false;
        MoveProfileRow(row, from, destination);
        return true;
    }

    private void MoveProfileRow(ProfileModRow row, int from, int destination)
    {
        ProfileMods.Move(from, destination);
        SelectedProfileMod = row;
        MarkProfileChanged();
    }

    public void CancelCurrentOperation() { _operation?.Cancel(); CancelWorkshopOperations(); CancelDetailPreview(); Status = T("OperationCancelled"); }

    private bool CanMove(int offset)
    {
        if (IsBusy || SelectedProfileMod is null) return false;
        var index = ProfileMods.IndexOf(SelectedProfileMod);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < ProfileMods.Count;
    }

    private void MarkProfileChanged()
    {
        ClearSaveCheck();
        SetWorkshopUpdateReport(null);
        InvalidateProfileChecks();
        for (var i = 0; i < ProfileMods.Count; i++) ProfileMods[i].Order = i + 1;
        IsProfileDirty = true;
        GameProfileApplied = false;
        SyncLocalModToggles();
        CaptureCurrentDraft();
        Changed(nameof(ProfileEmpty));
        RefreshCommands();
    }

    private void UpdateModDirectories(GameEnvironment environment)
    {
        _localEnvironment = environment;
        if (_userDirectory != environment.UserDataDirectory)
        {
            _userDirectory = environment.UserDataDirectory;
            Changed(nameof(UserDirectoryText));
        }
        var local = FormatDirectories(environment.LocalModDirectories, T("NoLocalModFolderFound"));
        var workshop = FormatDirectories(environment.WorkshopDirectories, T("NoDownloadedWorkshopFolders"));
        if (LocalModDirectoriesText != local)
        {
            LocalModDirectoriesText = local;
            Changed(nameof(LocalModDirectoriesText));
        }
        if (WorkshopDirectoriesText != workshop)
        {
            WorkshopDirectoriesText = workshop;
            Changed(nameof(WorkshopDirectoriesText));
        }
    }

    private static string FormatDirectories(IReadOnlyList<string> directories, string emptyText) =>
        directories.Count == 0 ? emptyText : string.Join(Environment.NewLine, directories);

    private AppSettings CurrentSettings() => new(Clean(SteamPath), Clean(GamePath), Clean(UserPath));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ApplyWorkspace(WorkspaceState state)
    {
        StopLocalModMonitoring();
        CaptureCurrentDraft();
        SteamPath = state.Settings.SteamDirectory ?? "";
        GamePath = state.Settings.GameDirectory ?? "";
        UserPath = state.Settings.UserDataDirectory ?? "";
        _gameVersion = state.Environment.GameVersion;
        UpdateWorkshopRecommendations();
        _gameDirectory = state.Environment.GameDirectory;
        var selectedId = SelectedProfile?.Id;
        SelectedProfile = null;
        SelectedInstalledMod = null;
        ApplyLocalModSnapshot(state.Scan with { Environment = state.Environment }, replace: true);
        Profiles.Clear();
        foreach (var profile in state.Profiles) Profiles.Add(profile);
        foreach (var draft in _drafts.Values)
            if (!Profiles.Any(p => p.Id == draft.Id)) Profiles.Add(draft);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        foreach (var property in new[] { nameof(GameVersionText), nameof(GameDirectoryText), nameof(ProfileCountText) })
            Changed(property);
        _localModSettings = state.Settings;
        StartLocalModMonitoring();
    }

    public void SetLocalModMonitoringEnabled(bool enabled)
    {
        if (_disposed || _localModMonitoringEnabled == enabled) return;
        _localModMonitoringEnabled = enabled;
        if (enabled)
        {
            StartLocalModMonitoring();
            if (IsWorkshop) RequestWorkshopPageRefresh();
        }
        else
        {
            StopLocalModMonitoring();
            InvalidateProfileChecks();
            InvalidatePortableTransfers();
            InvalidateDiagnostics();
        }
        ((AsyncCommand)CheckFileConflictsCommand).Refresh();
        RefreshDependencyCommands();
        RefreshPortableTransferCommands();
        RefreshDiagnosticCommands();
    }

    private void StartLocalModMonitoring()
    {
        if (_disposed || !_localModMonitoringEnabled || _localModMonitor is null || _localModSettings is null || _localModMonitoring is not null) return;
        var operation = new CancellationTokenSource();
        _localModMonitoring = operation;
        _ = MonitorLocalModsAsync(_localModSettings.ToDiscoveryOptions(), operation);
    }

    private async Task MonitorLocalModsAsync(DiscoveryOptions options, CancellationTokenSource operation)
    {
        var token = operation.Token;
        try
        {
            await foreach (var scan in _localModMonitor!.WatchAsync(options, token))
            {
                if (_disposed || token.IsCancellationRequested || !_localModMonitoringEnabled || !ReferenceEquals(_localModMonitoring, operation)) return;
                ApplyLocalModSnapshot(scan);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { /* 检测失败保留已载入的本地内容。 */ }
        finally
        {
            if (ReferenceEquals(_localModMonitoring, operation)) _localModMonitoring = null;
            operation.Dispose();
        }
    }

    private void StopLocalModMonitoring()
    {
        var operation = _localModMonitoring;
        _localModMonitoring = null;
        operation?.Cancel();
    }

    private void ApplyLocalModSnapshot(ModScanResult scan, string? removedModId = null, bool replace = false)
    {
        if (_workspace.LocalMods.Accept(scan, replace, removedModId) is not { } update) return;
        if (update.Scan.Environment is { } environment) UpdateModDirectories(environment);
        var mods = update.Scan.Mods;
        if (update.ModsChanged)
        {
            InvalidateProfileChecks(installationsChanged: true);
            var selectedId = SelectedInstalledMod?.Mod.Id;
            InstalledMods.Clear();
            foreach (var mod in mods) InstalledMods.Add(CreateInstalledRow(mod));
            ResolveImportedProfileRows(mods);
            SyncLocalModToggles();
            SelectedInstalledMod = InstalledMods.FirstOrDefault(row => row.Mod.Id == selectedId);
            _lastScan = DateTimeOffset.Now;
            foreach (var property in new[] { nameof(ModCountText), nameof(LastScan), nameof(IsLibraryEmpty), nameof(FilteredMods), nameof(NoSearchResults) }) Changed(property);
            NotifyLocalPaging();
            StartWorkshopCompatibilityLookup();
        }
        RefreshDownloadInstallations();
        var warnings = (update.Scan.Environment?.Warnings ?? []).Concat(update.Scan.Warnings).Distinct().ToArray();
        if (!Warnings.SequenceEqual(warnings))
        {
            Warnings.Clear();
            foreach (var warning in warnings) Warnings.Add(warning);
            Changed(nameof(HasWarnings));
        }
    }

    public Task CompatibilityLookupTask { get; private set; } = Task.CompletedTask;

    private void StartWorkshopCompatibilityLookup()
    {
        _compatibilityLookup?.Cancel();
        _compatibilityLookup?.Dispose();
        var operation = new CancellationTokenSource();
        _compatibilityLookup = operation;
        CompatibilityLookupTask = ResolveWorkshopCompatibilityAsync(operation);
    }

    private async Task ResolveWorkshopCompatibilityAsync(CancellationTokenSource operation)
    {
        var token = operation.Token;
        var rows = InstalledMods.Where(row => row.NeedsWorkshopCompatibility).ToArray();
        if (rows.Length == 0) return;
        try
        {
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var item = await _workshop.LookupAsync(row.Mod.WorkshopId!.Value.ToString(), token);
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_compatibilityLookup, operation)) return;
                    var status = WorkshopCatalog.EvaluateItem(item, _gameVersion).Compatibility;
                    if (status == CompatibilityStatus.Unknown) return;
                    OnUi(() =>
                    {
                        if (!ReferenceEquals(_compatibilityLookup, operation)) return;
                        row.UpdateCompatibility(status);
                        foreach (var download in Downloads.Where(download => download.Snapshot.PublishedFileId == row.Mod.WorkshopId))
                            UpdateDownloadCompatibility(download);
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { /* 工坊不可用时保留现有未知判定。 */ }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_compatibilityLookup, operation)) _compatibilityLookup = null;
            operation.Dispose();
        }
    }

    private void CaptureCurrentDraft()
    {
        if (SelectedProfile is null || !IsProfileDirty) return;
        _drafts[SelectedProfile.Id] = new ModProfile(SelectedProfile.Id, ProfileName, Clean(ProfileGameVersion),
            ProfileMods.Select(m => m.Mod).ToArray(), DateTimeOffset.UtcNow);
    }

    private async Task RunOperationAsync(string action, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        if (IsBusy || _disposed) return;
        CancelWorkshopOperations();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        ErrorMessage = null;
        LastError = null;
        IsBusy = true;
        Status = T("InProgress", action);
        try { await work(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Status = T("OperationCancelled"); }
        catch (Exception error)
        {
            LastError = error;
            ErrorMessage = T("Failed", action, error.Message);
            Status = T("OperationFailedSeeTheErrorDetails");
        }
        finally { _operation = null; IsBusy = false; }
        await RefreshWorkshopPageIfNeededAsync();
        await RefreshDownloadedModsAsync();
    }

    private void LaunchGame()
    {
        ErrorMessage = null;
        LastError = null;
        try
        {
            _navigator.LaunchGame();
            Status = T("SteamLaunchRequestedForContinueInSteam", Game.ShortName);
        }
        catch (Exception error)
        {
            LastError = error;
            ErrorMessage = T("CouldNotLaunchTheGameCheckThe", error.Message);
            Status = T("GameLaunchRequestFailedSeeTheError");
        }
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { RefreshCommand, LookupWorkshopCommand, SaveSettingsCommand, SaveProfileCommand, CancelCommand, NewProfileCommand, DeleteProfileCommand, AddModCommand, RemoveModCommand, MoveUpCommand, MoveDownCommand, DownloadWorkshopCommand, LaunchGameCommand, ApplyGameProfileCommand,
            ReadGameProfileCommand })
        {
            if (command is RelayCommand relay) relay.Refresh();
            if (command is AsyncCommand asyncCommand) asyncCommand.Refresh();
        }
        RefreshWorkshopCommands();
        RefreshInstalledModRemovalCommands();
        RefreshProfileTransferCommands();
        RefreshPlaysetSnapshotCommands();
        RefreshSaveBindingCommands();
        ((AsyncCommand)CheckWorkshopUpdatesCommand).Refresh();
        ((AsyncCommand)CheckFileConflictsCommand).Refresh();
        RefreshDependencyCommands();
        RefreshDiagnosticCommands();
        ((RelayCommand)SelectUiLanguageCommand).Refresh();
    }

    public void Dispose() { _disposed = true; UiLocale.LanguageChanged -= RefreshUiLanguage; StopLocalModMonitoring(); _operation?.Cancel(); _compatibilityLookup?.Cancel(); DisposeWorkshop(); DisposeDownloads(); }
}
