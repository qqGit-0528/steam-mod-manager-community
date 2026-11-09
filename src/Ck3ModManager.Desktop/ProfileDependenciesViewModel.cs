using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record DependencyFindingRow(string Message, ulong? WorkshopId)
{
    public bool HasWorkshopId => WorkshopId is > 0;
}

public sealed partial class MainWindowViewModel
{
    private ProfileDependencyService? _profileDependencies;
    private ProfileDependencyReport? _dependencyReport;
    private CancellationTokenSource? _dependencyOperation;
    private bool _queueingDependencies;

    public bool SupportsDependencyCheck => _profileDependencies is not null;
    public bool HasDependencyReport => _dependencyReport is not null;
    public bool CanDownloadDependencies => _downloads is not null && Game.SupportsNativeWorkshopDownloads &&
        _dependencyReport is { MissingWorkshopItems.Count: > 0 } && Path.IsPathFullyQualified(_userDirectory);
    public string DependencyDownloadLabel => T("ConfirmDependencyDownloads", _dependencyReport?.MissingWorkshopItems.Count ?? 0);
    public IReadOnlyList<DependencyFindingRow> DependencyFindings { get; private set; } = [];
    public IReadOnlyList<string> DependencyDownloadPreview { get; private set; } = [];
    public string DependencySummary => _dependencyReport is { } report
        ? T("CheckedFindingsAvailableMissingWorkshopItems", report.CheckedItems, report.Findings.Count, report.MissingWorkshopItems.Count)
        : "";

    public ICommand CheckDependenciesCommand { get; private set; } = null!;
    public ICommand DownloadDependenciesCommand { get; private set; } = null!;
    public ICommand OpenDependencyWorkshopCommand { get; private set; } = null!;

    private void InitializeDependencies(IWorkshopDependencySource? source)
    {
        if (source is not null) _profileDependencies = new(_workshop, source);
        CheckDependenciesCommand = new AsyncCommand(CheckDependenciesAsync, CanCheckDependencies);
        DownloadDependenciesCommand = new AsyncCommand(DownloadDependenciesAsync,
            () => CanCheckDependencies() && CanDownloadDependencies);
        OpenDependencyWorkshopCommand = new RelayCommand(parameter =>
        {
            if (parameter is ulong id && id > 0) _navigator.OpenWorkshop(id);
        }, () => !IsBusy);
    }

    private bool CanCheckDependencies() =>
        SupportsDependencyCheck && _localModMonitoringEnabled && !_disposed && IsProfiles && !IsBusy &&
        HasProfile && !HasProfileTransferPreview && ProfileMods.Any(row => row.Enabled);

    public Task CheckDependenciesAsync()
    {
        if (!CanCheckDependencies()) return Task.CompletedTask;
        SetDependencyReport(null);
        return RunDependencyOperationAsync(T("CheckDependencies"), async token =>
        {
            var profile = BuildGameProfile();
            var scan = await _workspace.ScanLocalModsAsync(CurrentSettings(), token);
            var result = await _profileDependencies!.CheckAsync(profile, scan.Mods, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            SetDependencyReport(result);
            Status = T("DependencyCheckComplete");
        });
    }

    public Task DownloadDependenciesAsync()
    {
        if (!CanCheckDependencies() || !CanDownloadDependencies || _dependencyReport is not { } review)
            return Task.CompletedTask;

        return RunDependencyOperationAsync(T("QueueDependencyDownloads"), async token =>
        {
            var scan = await _workspace.ScanLocalModsAsync(CurrentSettings(), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_dependencyReport, review)) return;

            _queueingDependencies = true;
            try
            {
                var queued = new InlineProgress<WorkshopDownloadSnapshot>(snapshot => { if (!_disposed) ApplyDownload(snapshot); });
                var result = await _profileDependencies!.QueueAsync(review, scan, _downloads!, queued, token);
                if (_disposed) return;
                SetDependencyReport(null);
                Status = result.Queued == 0 ? T("NoDependenciesNeedDownloading") : T("DependencyDownloadsQueued", result.Queued);
                if (result.Failures.Count > 0)
                    ErrorMessage = T("SomeDependenciesCouldNotBeQueued", string.Join(T("DetailSeparator"), result.Failures.Take(3)));
            }
            finally { _queueingDependencies = false; }
        });
    }

    private async Task RunDependencyOperationAsync(string action, Func<CancellationToken, Task> work)
    {
        using var operation = new CancellationTokenSource();
        _dependencyOperation = operation;
        try { await RunOperationAsync(action, work, operation.Token); }
        finally
        {
            if (ReferenceEquals(_dependencyOperation, operation)) _dependencyOperation = null;
        }
    }

    private void InvalidateProfileChecks(bool installationsChanged = false)
    {
        InvalidateFileConflicts();
        if (!installationsChanged || !_queueingDependencies) _dependencyOperation?.Cancel();
        SetDependencyReport(null);
    }

    private void SetDependencyReport(ProfileDependencyReport? report)
    {
        if (_dependencyReport is null && report is null) return;
        _dependencyReport = report;
        DependencyFindings = report?.Findings.Select(item => new DependencyFindingRow(item.Message, item.WorkshopId)).ToArray() ?? [];
        DependencyDownloadPreview = report?.MissingWorkshopItems
            .Select(item => T("Workshop4", item.Title, item.PublishedFileId)).ToArray() ?? [];
        Changed(nameof(HasDependencyReport));
        Changed(nameof(DependencyDownloadPreview));
        Changed(nameof(DependencyFindings));
        Changed(nameof(DependencySummary));
        Changed(nameof(CanDownloadDependencies));
        Changed(nameof(DependencyDownloadLabel));
        RefreshDependencyCommands();
    }

    private void RefreshDependencyCommands()
    {
        ((AsyncCommand)CheckDependenciesCommand).Refresh();
        ((AsyncCommand)DownloadDependenciesCommand).Refresh();
        ((RelayCommand)OpenDependencyWorkshopCommand).Refresh();
    }
}
