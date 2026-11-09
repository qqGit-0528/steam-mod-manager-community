using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record PackageModRow(int Order, PlaysetPackageMod Mod)
{
    public string Name => Mod.Name;
    public string Version => Mod.ModVersion ?? T("Undeclared");
    public string State => Mod.Enabled ? T("EnabledFilesIncluded") : T("DisabledListEntry");
}

public sealed partial class MainWindowViewModel
{
    private LocalModImportPreview? _localModImport;
    private PlaysetPackagePreview? _playsetPackageImport;
    private CancellationTokenSource? _portableTransfer;

    public bool SupportsLocalModImport => _workspace.HasLocalModImport;
    public bool HasLocalModImportPreview => _localModImport is not null;
    public bool CanPreviewLocalModImport => SupportsLocalModImport && IsOverview && _localModMonitoringEnabled &&
        !_disposed && !IsBusy && !HasLocalModImportPreview;
    public string LocalModImportSummary => _localModImport is { } mod
        ? T("ModCK3Files", mod.Name, mod.ModVersion ?? T("Undeclared"), mod.SupportedGameVersion ?? T("Undeclared"), mod.FileCount, FormatPackageSize(mod.Bytes))
        : "";
    public string LocalModImportPaths => _localModImport is { } mod ? T("SourceInstallTo", mod.SourcePath, mod.TargetPath) : "";

    public bool SupportsPlaysetPackageTransfer => _workspace.HasPlaysetPackageTransfer;
    public bool HasPlaysetPackagePreview => _playsetPackageImport is not null;
    public bool HasProfileTransferPreview => HasProfileImportPreview || HasPlaysetPackagePreview;
    public bool CanPreviewPlaysetPackage => SupportsPlaysetPackageTransfer && IsProfiles && _localModMonitoringEnabled &&
        !_disposed && !IsBusy && !HasProfileTransferPreview;
    public bool CanExportPlaysetPackage => CanPreviewPlaysetPackage && HasProfile && ProfileMods.Any(row => row.Enabled);
    public string PlaysetPackageSummary => _playsetPackageImport is { } package
        ? T("RequiresCK3InstalledGame", package.Name, package.GameVersion, _gameVersion ?? T("VersionUnknown"), FormatPackageSize(package.Bytes))
        : "";
    public IReadOnlyList<PackageModRow> PlaysetPackageRows { get; private set; } = [];

    public ICommand ConfirmLocalModInstallCommand { get; private set; } = null!;
    public ICommand CancelLocalModInstallCommand { get; private set; } = null!;
    public ICommand ConfirmPlaysetPackageImportCommand { get; private set; } = null!;
    public ICommand CancelPlaysetPackageImportCommand { get; private set; } = null!;

    private void InitializePortableModTransfers()
    {
        ConfirmLocalModInstallCommand = new AsyncCommand(ConfirmLocalModInstallAsync,
            () => HasLocalModImportPreview && IsOverview && !IsBusy && _localModMonitoringEnabled);
        CancelLocalModInstallCommand = new RelayCommand(_ => SetLocalModImportPreview(null), () => !IsBusy);
        ConfirmPlaysetPackageImportCommand = new AsyncCommand(ConfirmPlaysetPackageImportAsync,
            () => HasPlaysetPackagePreview && IsProfiles && !IsBusy && _localModMonitoringEnabled);
        CancelPlaysetPackageImportCommand = new RelayCommand(_ => SetPlaysetPackagePreview(null), () => !IsBusy);
    }

    public Task PreviewLocalModImportAsync(string sourcePath)
    {
        if (!CanPreviewLocalModImport) return Task.CompletedTask;
        return RunPortableTransferAsync(T("PreviewLocalMod"), async token =>
        {
            var review = await _workspace.PreviewLocalModImportAsync(CurrentSettings(), sourcePath, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            CancelInstalledModRemoval();
            SetLocalModImportPreview(review);
            Status = T("ReviewTheModSourceAndInstallationPath");
        });
    }

    public Task ConfirmLocalModInstallAsync()
    {
        if (_localModImport is not { } review || !ConfirmLocalModInstallCommand.CanExecute(null)) return Task.CompletedTask;
        return RunPortableTransferAsync(T("InstallLocalMod"), async token =>
        {
            var settings = CurrentSettings();
            var installedPath = await _workspace.InstallLocalModAsync(settings, review, token);
            if (_disposed) return;
            SetLocalModImportPreview(null);
            Status = T("Installed", review.Name);
            await RefreshAfterModTransferAsync(settings);
            if (!_disposed) SelectedInstalledMod = InstalledMods.FirstOrDefault(row => row.ContentPath == installedPath);
        });
    }

    public Task ExportPlaysetPackageAsync(string destinationPath)
    {
        if (!CanExportPlaysetPackage) return Task.CompletedTask;
        return RunPortableTransferAsync(T("ExportPack"), async token =>
        {
            await _workspace.ExportPlaysetPackageAsync(CurrentSettings(), BuildGameProfile(), destinationPath, token);
            if (!_disposed) Status = T("PlaysetPackExported");
        });
    }

    public Task PreviewPlaysetPackageAsync(string sourcePath)
    {
        if (!CanPreviewPlaysetPackage) return Task.CompletedTask;
        return RunPortableTransferAsync(T("PreviewPlaysetPack"), async token =>
        {
            var package = await _workspace.PreviewPlaysetPackageAsync(sourcePath, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            SetPlaysetPackagePreview(package);
            Status = T("ReviewTheCK3VersionEnabledModsAnd");
        });
    }

    public Task ConfirmPlaysetPackageImportAsync()
    {
        if (_playsetPackageImport is not { } review || !ConfirmPlaysetPackageImportCommand.CanExecute(null))
            return Task.CompletedTask;
        return RunPortableTransferAsync(T("ImportPack"), async token =>
        {
            var settings = CurrentSettings();
            var profile = await _workspace.ImportPlaysetPackageAsync(settings, review, token);
            if (_disposed) return;
            SetPlaysetPackagePreview(null);
            PresentRestoredProfile(profile);
            Status = T("FilesVerifiedAndRestoredToASeparate");
            await RefreshAfterModTransferAsync(settings);
        });
    }

    private async Task RefreshAfterModTransferAsync(AppSettings settings)
    {
        try
        {
            var scan = await _workspace.ScanLocalModsAsync(settings);
            if (!_disposed) ApplyLocalModSnapshot(scan);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed) ErrorMessage = T("FilesSavedButTheLocalLibraryCould", error.Message); }
    }

    private async Task RunPortableTransferAsync(string label, Func<CancellationToken, Task> work)
    {
        using var operation = new CancellationTokenSource();
        _portableTransfer = operation;
        try { await RunOperationAsync(label, work, operation.Token); }
        finally { if (ReferenceEquals(_portableTransfer, operation)) _portableTransfer = null; }
    }

    private void InvalidatePortableTransfers()
    {
        _portableTransfer?.Cancel();
        SetLocalModImportPreview(null);
        SetPlaysetPackagePreview(null);
    }

    private void SetLocalModImportPreview(LocalModImportPreview? preview)
    {
        if (_localModImport is null && preview is null) return;
        _localModImport = preview;
        Changed(nameof(HasLocalModImportPreview));
        Changed(nameof(LocalModImportSummary));
        Changed(nameof(LocalModImportPaths));
        RefreshInstalledModRemovalCommands();
        RefreshPortableTransferCommands();
    }

    private void SetPlaysetPackagePreview(PlaysetPackagePreview? preview)
    {
        if (_playsetPackageImport is null && preview is null) return;
        _playsetPackageImport = preview;
        PlaysetPackageRows = preview?.Mods.Select((mod, index) => new PackageModRow(index + 1, mod)).ToArray() ?? [];
        Changed(nameof(HasPlaysetPackagePreview));
        Changed(nameof(HasProfileTransferPreview));
        Changed(nameof(PlaysetPackageSummary));
        Changed(nameof(PlaysetPackageRows));
        RefreshProfileTransferCommands();
    }

    private void RefreshPortableTransferCommands()
    {
        ((AsyncCommand)ConfirmLocalModInstallCommand).Refresh();
        ((RelayCommand)CancelLocalModInstallCommand).Refresh();
        ((AsyncCommand)ConfirmPlaysetPackageImportCommand).Refresh();
        ((RelayCommand)CancelPlaysetPackageImportCommand).Refresh();
        Changed(nameof(CanPreviewLocalModImport));
        Changed(nameof(CanPreviewPlaysetPackage));
        Changed(nameof(CanExportPlaysetPackage));
    }

    private static string FormatPackageSize(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:N1} KiB" : $"{bytes / (1024d * 1024):N1} MiB";
}
