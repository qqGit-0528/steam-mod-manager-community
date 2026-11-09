using static Ck3ModManager.Core.AppText;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using QRCoder;

namespace Ck3ModManager.Desktop;

public sealed class DownloadRow : ObservableObject, IDisposable
{
    private WorkshopDownloadSnapshot _snapshot;
    private LocalInstallation _installation = new(LocalInstallationState.Unknown);
    private CompatibilityStatus _compatibility;
    private Bitmap? _qrCode;
    private readonly Func<WorkshopDownloadSnapshot, Task>? _remove;
    private bool _removing;
    private bool _retrying;
    public DownloadRow(WorkshopDownloadSnapshot snapshot, Action<Guid> cancel, Action<string> open,
        Func<WorkshopDownloadSnapshot, Task>? retry = null, Func<WorkshopDownloadSnapshot, Task>? remove = null)
    {
        _snapshot = snapshot;
        _remove = remove;
        CancelCommand = new RelayCommand(_ => cancel(Snapshot.TaskId), () => CanCancel);
        OpenDirectoryCommand = new RelayCommand(_ => open(_installation.OpenDirectory!), () => HasInstalledPath);
        RetryCommand = new AsyncCommand(async () =>
        {
            if (!CanRetry) return;
            SetRetrying(true);
            try { if (retry is not null) await retry(Snapshot); }
            finally { SetRetrying(false); }
        }, () => CanRetry && retry is not null);
        RemoveCommand = new AsyncCommand(RemoveAsync, () => CanRemove);
        Update(snapshot);
    }
    public WorkshopDownloadSnapshot Snapshot => _snapshot;
    public string Title => Snapshot.Title;
    public string Compatibility => CompatibilityDisplay.Label(_compatibility);
    public bool IsCompatibilityMatched => _compatibility == CompatibilityStatus.DeclaredMatch;
    public bool IsCompatibilityMismatch => _compatibility == CompatibilityStatus.DeclaredMismatch;
    public bool IsSteamClient => Snapshot.Owner == WorkshopDownloadOwner.SteamClient;
    public string CancelText => T("StopDownload");
    public string CancelTooltip => IsSteamClient
        ? T("StopTrackingTheDownloadHereSteamMay")
        : T("StopThisDownloadAndKeepPartialFiles");
    public string RemoveTooltip => IsSteamClient
        ? T("StopTrackingAndRemoveTheTaskSteam")
        : T("StopAndRemoveTheTaskPartialFiles");
    public string StateText => Snapshot.State switch
    {
        WorkshopDownloadState.Queued => IsSteamClient ? T("QueuedInSteam") : T("Queued"), WorkshopDownloadState.Resolving => T("PreparingDownload"),
        WorkshopDownloadState.AwaitingAuthentication => T("WaitingForAuthorization"), WorkshopDownloadState.Downloading => T("Downloading"),
        WorkshopDownloadState.Verifying => T("Verifying"), WorkshopDownloadState.Installing => T("Installing"),
        WorkshopDownloadState.Completed => T("DownloadComplete"), WorkshopDownloadState.Failed => T("DownloadFailed"),
        WorkshopDownloadState.Cancelled => T("Cancelled"), _ => IsSteamClient ? T("TrackingStopped") : T("PreviousDownloadInterrupted")
    };
    public string Message => Snapshot.Message ?? "";
    public string InstalledPath => Snapshot.InstalledPath ?? "";
    public bool HasInstallationStatus => Snapshot.State == WorkshopDownloadState.Completed;
    public string InstallationStatus => _installation.State switch
    {
        LocalInstallationState.Installed => T("CurrentlyInstalled"),
        LocalInstallationState.Missing => T("CurrentlyNotInstalled"),
        _ => T("CurrentInstallationUnconfirmed")
    };
    public bool HasInstalledPath => HasInstallationStatus && _installation is { State: LocalInstallationState.Installed, OpenDirectory: not null };
    public double Percent => IsSteamClient && Snapshot.State == WorkshopDownloadState.Completed ? 100
        : Snapshot.TotalBytes is > 0 ? Math.Clamp(100d * Snapshot.Bytes / Snapshot.TotalBytes.Value, 0, 100) : 0;
    public bool IsIndeterminate => !Snapshot.IsTerminal && (Snapshot.TotalBytes is not > 0 || Snapshot.State != WorkshopDownloadState.Downloading);
    public string ProgressText => IsSteamClient && Snapshot is { State: WorkshopDownloadState.Completed, InstalledSizeBytes: { } size }
        ? T("SizeAtInstall", Size(size)) : Snapshot.TotalBytes is > 0
        ? $"{Size(Snapshot.Bytes)} / {Size(Snapshot.TotalBytes.Value)} · {Percent:F1}%" : Snapshot.Bytes > 0 ? Size(Snapshot.Bytes) : "";
    public string SpeedText => Snapshot.State == WorkshopDownloadState.Downloading && Snapshot.BytesPerSecond > 0 ? $"{Size((long)Snapshot.BytesPerSecond)}/s" : "";
    public bool CanCancel => !Snapshot.IsTerminal && !_removing && Snapshot.State != WorkshopDownloadState.Installing;
    public Bitmap? QrCode => _qrCode;
    public bool HasQrCode => _qrCode is not null;
    public ICommand CancelCommand { get; }
    public ICommand OpenDirectoryCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveCommand { get; }
    public bool HasRemoveAction => _remove is not null;
    public bool CanRemove => !_removing && !_retrying && HasRemoveAction;
    public bool CanRetry => Snapshot.IsTerminal && !_removing && !_retrying;
    public string RetryText => _retrying ? T("Checking") : Snapshot.State switch
    {
        WorkshopDownloadState.Completed when _installation.State == LocalInstallationState.Missing => T("DownloadAgain"),
        WorkshopDownloadState.Completed => T("CheckForUpdates"),
        _ => T("RetryDownload")
    };

    private void SetRetrying(bool value)
    {
        _retrying = value;
        foreach (var property in new[] { nameof(CanRetry), nameof(RetryText), nameof(CanRemove) }) Changed(property);
        ((AsyncCommand)RetryCommand).Refresh(); ((AsyncCommand)RemoveCommand).Refresh();
    }

    public void Update(WorkshopDownloadSnapshot snapshot)
    {
        if (snapshot.UpdatedAt < _snapshot.UpdatedAt) return;
        var oldUrl = _snapshot.AuthenticationUrl;
        _snapshot = snapshot;
        if (oldUrl != snapshot.AuthenticationUrl || _qrCode is null && snapshot.AuthenticationUrl is not null)
        {
            _qrCode?.Dispose(); _qrCode = null;
            if (!IsSteamClient && snapshot.AuthenticationUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "s.team")
            {
                using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
                using var qr = new PngByteQRCode(data);
                using var stream = new MemoryStream(qr.GetGraphic(6));
                _qrCode = new Bitmap(stream);
            }
        }
        foreach (var property in new[] { nameof(Snapshot), nameof(Title), nameof(StateText), nameof(Message), nameof(InstalledPath), nameof(HasInstalledPath), nameof(HasInstallationStatus),
            nameof(IsSteamClient), nameof(CancelText), nameof(CancelTooltip), nameof(RemoveTooltip), nameof(CanRetry), nameof(RetryText), nameof(Percent), nameof(ProgressText), nameof(SpeedText), nameof(IsIndeterminate), nameof(CanCancel), nameof(CanRemove), nameof(QrCode), nameof(HasQrCode) }) Changed(property);
        ((RelayCommand)CancelCommand).Refresh(); ((RelayCommand)OpenDirectoryCommand).Refresh(); ((AsyncCommand)RetryCommand).Refresh();
        ((AsyncCommand)RemoveCommand).Refresh();
    }
    public void UpdateInstallation(LocalInstallation installation)
    {
        if (_installation == installation) return;
        _installation = installation;
        Changed(nameof(InstallationStatus));
        Changed(nameof(HasInstalledPath));
        Changed(nameof(RetryText));
        ((RelayCommand)OpenDirectoryCommand).Refresh();
    }

    public void UpdateCompatibility(CompatibilityStatus status)
    {
        if (_compatibility == status) return;
        _compatibility = status;
        Changed(nameof(Compatibility));
        Changed(nameof(IsCompatibilityMatched));
        Changed(nameof(IsCompatibilityMismatch));
    }

    public async Task RemoveAsync()
    {
        if (!CanRemove) return;
        SetRemoving(true);
        try { await _remove!(Snapshot); }
        finally { SetRemoving(false); }
    }
    private void SetRemoving(bool value)
    {
        _removing = value;
        foreach (var property in new[] { nameof(CanRemove), nameof(CanRetry), nameof(CanCancel) }) Changed(property);
        ((AsyncCommand)RemoveCommand).Refresh(); ((AsyncCommand)RetryCommand).Refresh(); ((RelayCommand)CancelCommand).Refresh();
    }
    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F2} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):F1} MB" : bytes >= 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes} B";
    public void Dispose() { _qrCode?.Dispose(); _qrCode = null; }
}

public sealed partial class MainWindowViewModel
{
    private WorkshopDownloadService? _downloads;
    private bool _downloadRefreshPending;
    private bool _refreshingDownloadedMods;
    public ObservableCollection<DownloadRow> Downloads { get; } = [];
    public bool IsDownloads => Section == "downloads";
    public bool HasDownloads => Downloads.Count > 0;
    public ICommand DownloadWorkshopCommand { get; private set; } = null!;

    private void InitializeDownloads(WorkshopDownloadService? downloads)
    {
        _downloads = downloads;
        DownloadWorkshopCommand = new AsyncCommand(DownloadSelectedWorkshopAsync, () => !IsBusy && !_disposed && CanDownloadWorkshopItem);
        if (downloads is not null) { downloads.Changed += OnDownloadChanged; downloads.Installed += OnDownloadInstalled; }
    }

    private async Task DownloadSelectedWorkshopAsync()
    {
        if (_downloads is null) { ErrorMessage = T("DownloadsAreUnavailableInThisEnvironment"); return; }
        if (WorkshopItem is not { } item || !CanDownloadWorkshopItem) return;
        try
        {
            if (!Path.IsPathFullyQualified(_userDirectory)) throw new InvalidOperationException(T("ConfirmTheGameUserDataFolderIn"));
            var task = _downloads.Start(item, _userDirectory);
            Section = "downloads";
            Status = T("DownloadQueuedFollowItsProgressInThe");
            ErrorMessage = null;
            await task.IdentityReady;
            if (!_disposed) ApplyDownload(task.Snapshot);
        }
        catch (Exception error) { ErrorMessage = T("CouldNotCreateTheDownloadTask", error.Message); }
    }

    private void OnDownloadChanged(WorkshopDownloadSnapshot snapshot) => OnUi(() =>
    {
        if (!_disposed && _downloads?.IsCurrent(snapshot) == true) ApplyDownload(snapshot);
    });
    private void OnDownloadInstalled(WorkshopDownloadSnapshot snapshot) => OnUi(() =>
    { if (!_disposed) { _downloadRefreshPending = true; _ = RefreshDownloadedModsAsync(); } });
    private static void OnUi(Action action) { if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action); }

    private void ApplyDownload(WorkshopDownloadSnapshot snapshot)
    {
        if (snapshot.AppId != Game.AppId) return;
        var row = Downloads.FirstOrDefault(row => row.Snapshot.TaskId == snapshot.TaskId);
        if (snapshot.Owner == WorkshopDownloadOwner.SteamClient)
        {
            var sameItem = Downloads.Where(r => r.Snapshot.Owner == WorkshopDownloadOwner.SteamClient &&
                r.Snapshot.AppId == snapshot.AppId && r.Snapshot.PublishedFileId == snapshot.PublishedFileId).ToArray();
            if (sameItem.Any(r => r.Snapshot.UpdatedAt > snapshot.UpdatedAt)) return;
            foreach (var duplicate in sameItem.Where(r => !ReferenceEquals(r, row)))
            { Downloads.Remove(duplicate); duplicate.Dispose(); }
        }
        if (row is null)
        {
            row = new DownloadRow(snapshot, id => _downloads?.Tasks.FirstOrDefault(task => task.Snapshot.TaskId == id)?.Cancel(), path =>
            { try { _navigator.OpenDirectory(path); } catch (Exception error) { ErrorMessage = error.Message; } }, RetryDownloadAsync, RemoveDownloadAsync);
            Downloads.Insert(0, row);
            Changed(nameof(HasDownloads));
        }
        else row.Update(snapshot);
        RefreshDownloadInstallation(row);
        if (snapshot.IsTerminal && Downloads.Count > WorkshopDownloadHistory.RecentLimit)
        {
            foreach (var expired in Downloads.Where(candidate => candidate.Snapshot.IsTerminal)
                         .OrderByDescending(candidate => candidate.Snapshot.UpdatedAt)
                         .Skip(WorkshopDownloadHistory.RecentLimit).ToArray())
            {
                Downloads.Remove(expired);
                expired.Dispose();
            }
        }
    }

    private void RefreshDownloadInstallations()
    {
        foreach (var row in Downloads) RefreshDownloadInstallation(row);
    }

    private void RefreshDownloadInstallation(DownloadRow row)
    {
        row.UpdateInstallation(_workspace.LocalMods.FindInstallation(row.Snapshot));
        UpdateDownloadCompatibility(row);
    }

    private void UpdateDownloadCompatibility(DownloadRow row)
    {
        var status = CompatibilityStatus.Unknown;
        if (row.HasInstalledPath && _workspace.LocalMods.FindInstalledMod(row.Snapshot) is { } mod)
        {
            status = CompatibilityEvaluator.Evaluate(mod.SupportedGameVersion, _gameVersion).Status;
            if (status == CompatibilityStatus.Unknown)
                status = InstalledMods.FirstOrDefault(installed => installed.Mod.Id == mod.Id)?.ResolvedCompatibility
                    ?? CompatibilityStatus.Unknown;
        }
        if (status == CompatibilityStatus.Unknown)
            status = WorkshopCatalog.EvaluateGameVersions(row.Snapshot.WorkshopGameVersions, _gameVersion);
        row.UpdateCompatibility(status);
    }

    private async Task LoadDownloadHistoryAsync(CancellationToken token)
    {
        if (_downloads is null) return;
        try
        {
            foreach (var snapshot in await _downloads.ReadHistoryAsync(token))
                if (!_disposed && _downloads.HasHistory(snapshot) && !Downloads.Any(row => row.Snapshot.TaskId == snapshot.TaskId))
                    ApplyDownload(snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { if (!_disposed) Status = T("CouldNotLoadDownloadHistory", error.Message); }
    }

    private async Task RemoveDownloadAsync(WorkshopDownloadSnapshot snapshot)
    {
        if (_downloads is null || _disposed) return;
        try
        {
            ErrorMessage = null;
            Status = T("StoppingAndRemovingDownload");
            if (!await _downloads.RemoveAsync(snapshot))
                throw new InvalidOperationException(T("TheTaskChangedOnDiskReloadDownload"));
            if (_disposed) return;
            var row = Downloads.FirstOrDefault(candidate => candidate.Snapshot.TaskId == snapshot.TaskId);
            if (row is not null) { Downloads.Remove(row); row.Dispose(); }
            Changed(nameof(HasDownloads));
            Status = T("TaskRemoved");
        }
        catch (Exception error)
        { if (!_disposed) { ErrorMessage = T("CouldNotRemoveTheTask", error.Message); Status = T("TaskWasNotRemoved"); } }
    }

    private async Task RetryDownloadAsync(WorkshopDownloadSnapshot snapshot)
    {
        if (_downloads is null || _disposed || IsBusy) return;
        try
        {
            var item = await _workshop.LookupAsync(snapshot.PublishedFileId.ToString());
            if (_disposed || IsBusy) return;
            if (!Path.IsPathFullyQualified(_userDirectory)) throw new InvalidOperationException(T("ConfirmTheUserDataFolderFirst"));
            var task = _downloads.Retry(snapshot, item, _userDirectory);
            await task.IdentityReady;
            if (_disposed) return;
            ApplyDownload(task.Snapshot);
            await task.Completion;
            if (!_disposed && task.Snapshot.IsUpToDate) Status = T("AlreadyUpToDate");
        }
        catch (Exception error) { if (!_disposed) ErrorMessage = T("CouldNotRetryTheDownload", error.Message); }
    }

    private async Task RefreshDownloadedModsAsync()
    {
        if (!_downloadRefreshPending || _refreshingDownloadedMods || IsBusy || _disposed) return;
        _refreshingDownloadedMods = true;
        try
        {
            while (_downloadRefreshPending && !IsBusy && !_disposed)
            {
                _downloadRefreshPending = false;
                var settings = _localModSettings ?? CurrentSettings();
                var scan = await _workspace.ScanLocalModsAsync(settings);
                if (_disposed || settings != (_localModSettings ?? CurrentSettings())) continue;
                if (IsBusy) { _downloadRefreshPending = true; break; }
                // 扫描期间又完成了一次安装时，合并重扫，不让旧快照覆盖新安装。
                if (_downloadRefreshPending) continue;
                ApplyLocalModSnapshot(scan);
                Status = T("DownloadInstalledAndLocalLibraryRefreshed");
            }
        }
        catch (Exception error) { if (!_disposed) ErrorMessage = T("TheDownloadIsInstalledButTheLocal", error.Message); }
        finally { _refreshingDownloadedMods = false; }
    }

    private void DisposeDownloads()
    {
        if (_downloads is not null) { _downloads.Changed -= OnDownloadChanged; _downloads.Installed -= OnDownloadInstalled; }
        foreach (var row in Downloads) row.Dispose();
    }
}
