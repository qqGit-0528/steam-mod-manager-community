using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed partial class MainWindowViewModel
{
    private InstalledModRow? _pendingInstalledModRemoval;
    public ICommand PrepareInstalledModRemovalCommand { get; private set; } = null!;
    public ICommand DeleteInstalledModCommand { get; private set; } = null!;
    public ICommand CancelInstalledModRemovalCommand { get; private set; } = null!;
    public bool HasPendingInstalledModRemoval => _pendingInstalledModRemoval is not null;
    public string InstalledModRemovalPrompt => _pendingInstalledModRemoval is { } row
        ? T("PermanentlyDeleteFromDiskExitTheGame", row.Name, (row.Mod.Management == ModInstallManagement.Steam ? T("ThisAlsoUnsubscribesFromTheSteamItem") : ""), row.ContentPath)
        : "";

    private void InitializeInstalledModRemoval()
    {
        PrepareInstalledModRemovalCommand = new RelayCommand(_ => SetPendingInstalledModRemoval(SelectedInstalledMod),
            () => CanRemoveInstalledMod && !HasPendingInstalledModRemoval);
        DeleteInstalledModCommand = new AsyncCommand(DeleteInstalledModAsync,
            () => CanRemoveInstalledMod && ReferenceEquals(SelectedInstalledMod, _pendingInstalledModRemoval));
        CancelInstalledModRemovalCommand = new RelayCommand(_ => CancelInstalledModRemoval());
    }

    private bool CanRemoveInstalledMod => IsOverview && !IsBusy && !_disposed && !HasLocalModImportPreview &&
        _workspace.HasInstalledModRemoval && SelectedInstalledMod?.Mod is { IsAvailable: true, Management: not ModInstallManagement.External } &&
        (SelectedInstalledMod.Mod.Management != ModInstallManagement.Steam || Game.SupportsNativeWorkshopDownloads);

    private void RefreshInstalledModRemovalCommands()
    {
        ((RelayCommand)PrepareInstalledModRemovalCommand).Refresh();
        ((AsyncCommand)DeleteInstalledModCommand).Refresh();
    }

    private void CancelInstalledModRemoval() => SetPendingInstalledModRemoval(null);

    private void SetPendingInstalledModRemoval(InstalledModRow? row)
    {
        if (ReferenceEquals(_pendingInstalledModRemoval, row)) return;
        _pendingInstalledModRemoval = row;
        Changed(nameof(HasPendingInstalledModRemoval));
        Changed(nameof(InstalledModRemovalPrompt));
        RefreshInstalledModRemovalCommands();
    }

    public async Task DeleteInstalledModAsync()
    {
        if (!DeleteInstalledModCommand.CanExecute(null) || SelectedInstalledMod is not { } row) return;
        CancelInstalledModRemoval();
        await RunOperationAsync(T("DeleteMod"), async token =>
        {
            // 避免删除前开始的监听扫描晚到并恢复已经移除的行。
            StopLocalModMonitoring();
            try
            {
                var scan = await _workspace.RemoveInstalledModAsync(_localModSettings ?? CurrentSettings(), row.Mod, token);
                if (_disposed) return;
                ApplyLocalModSnapshot(scan, row.Mod.Id);
                if (ProfileMods.Any(entry => entry.Enabled && entry.Mod.InstalledModId == row.Mod.Id))
                    GameProfileApplied = false;
                Status = T("DeletedFromDisk", row.Name);
            }
            finally { StartLocalModMonitoring(); }
        });
    }
}
