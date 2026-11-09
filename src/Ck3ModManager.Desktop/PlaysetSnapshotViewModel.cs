using static Ck3ModManager.Core.AppText;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record PlaysetSnapshotRow(PlaysetSnapshot Snapshot)
{
    public string Label => $"{Snapshot.Name} · {Snapshot.CreatedAt.ToLocalTime():MM-dd HH:mm}";
    public string Detail => T("CK3EnabledMods", Snapshot.GameVersion, Snapshot.EnabledCount);
}

public sealed partial class MainWindowViewModel
{
    private PlaysetSnapshotRow? _selectedPlaysetSnapshot;
    private Guid? _pendingDeleteSnapshotId;

    public bool SupportsPlaysetSnapshots => _workspace.HasPlaysetSnapshots;
    public ObservableCollection<PlaysetSnapshotRow> PlaysetSnapshots { get; } = [];
    public PlaysetSnapshotRow? SelectedPlaysetSnapshot
    {
        get => _selectedPlaysetSnapshot;
        set
        {
            if (Set(ref _selectedPlaysetSnapshot, value))
            {
                if (_pendingDeleteSnapshotId != value?.Snapshot.Id)
                {
                    _pendingDeleteSnapshotId = null;
                    Changed(nameof(DeletePlaysetSnapshotLabel));
                }
                RefreshPlaysetSnapshotCommands();
            }
        }
    }

    public ICommand CapturePlaysetSnapshotCommand { get; private set; } = null!;
    public ICommand RestorePlaysetSnapshotCommand { get; private set; } = null!;
    public ICommand DeletePlaysetSnapshotCommand { get; private set; } = null!;
    public string DeletePlaysetSnapshotLabel => _pendingDeleteSnapshotId is not null ? T("ClickAgainToDelete") : T("DeleteSnapshot");
    public bool CanDeletePlaysetSnapshot => SupportsPlaysetSnapshots && !IsBusy && SelectedPlaysetSnapshot is not null;

    private void InitializePlaysetSnapshots()
    {
        CapturePlaysetSnapshotCommand = new AsyncCommand(CapturePlaysetSnapshotAsync, () =>
            SupportsPlaysetSnapshots && !IsBusy && HasProfile && ProfileMods.Any(mod => mod.Enabled));
        RestorePlaysetSnapshotCommand = new AsyncCommand(RestorePlaysetSnapshotAsync, () =>
            SupportsPlaysetSnapshots && !IsBusy && SelectedPlaysetSnapshot is not null);
        DeletePlaysetSnapshotCommand = new AsyncCommand(DeletePlaysetSnapshotAsync, () => CanDeletePlaysetSnapshot);
    }

    private async Task LoadPlaysetSnapshotsAsync(CancellationToken token)
    {
        if (!SupportsPlaysetSnapshots) return;
        var snapshots = await _workspace.ListPlaysetSnapshotsAsync(token);
        PlaysetSnapshots.Clear();
        foreach (var snapshot in snapshots) PlaysetSnapshots.Add(new PlaysetSnapshotRow(snapshot));
        if (_pendingDeleteSnapshotId is { } pending && PlaysetSnapshots.All(row => row.Snapshot.Id != pending))
        {
            _pendingDeleteSnapshotId = null;
            Changed(nameof(DeletePlaysetSnapshotLabel));
        }
        SelectedPlaysetSnapshot = PlaysetSnapshots.FirstOrDefault();
    }

    private Task CapturePlaysetSnapshotAsync() => RunOperationAsync(T("SavePlaysetSnapshot"), async token =>
    {
        if (SelectedProfile is null) throw new InvalidOperationException(T("SelectAProfileFirst"));
        var profile = new ModProfile(SelectedProfile.Id, ProfileName.Trim(), Clean(ProfileGameVersion),
            ProfileMods.Select(mod => mod.Mod).ToArray(), DateTimeOffset.UtcNow);
        var snapshot = await _workspace.CapturePlaysetSnapshotAsync(CurrentSettings(), profile, token);
        var row = new PlaysetSnapshotRow(snapshot);
        PlaysetSnapshots.Insert(0, row);
        SelectedPlaysetSnapshot = row;
        Status = T("SavedCK3SnapshotEnabledMods", snapshot.GameVersion, snapshot.EnabledCount);
    });

    private Task RestorePlaysetSnapshotAsync() => RunOperationAsync(T("RestorePlaysetSnapshot"), async token =>
    {
        if (SelectedPlaysetSnapshot is { } selected)
            await RestorePlaysetSnapshotCoreAsync(selected.Snapshot.Id, token);
    });

    private Task DeletePlaysetSnapshotAsync() => RunOperationAsync(T("DeletePlaysetSnapshot"), async token =>
    {
        if (SelectedPlaysetSnapshot is not { } row) return;
        if (_pendingDeleteSnapshotId != row.Snapshot.Id)
        {
            _pendingDeleteSnapshotId = row.Snapshot.Id;
            Changed(nameof(DeletePlaysetSnapshotLabel));
            Status = T("ClickAgainToDeleteTheSnapshotAnd");
            return;
        }
        _pendingDeleteSnapshotId = null;
        Changed(nameof(DeletePlaysetSnapshotLabel));
        await _workspace.DeletePlaysetSnapshotAsync(CurrentSettings(), row.Snapshot.Id, token);
        if (Profiles.FirstOrDefault(item => item.Id == row.Snapshot.Id) is { } restored)
        {
            await _workspace.DeleteProfileAsync(row.Snapshot.Id, token);
            Profiles.Remove(restored);
            _drafts.Remove(row.Snapshot.Id);
            if (SelectedProfile?.Id == row.Snapshot.Id) SelectedProfile = Profiles.FirstOrDefault();
        }
        PlaysetSnapshots.Remove(row);
        SelectedPlaysetSnapshot = PlaysetSnapshots.FirstOrDefault();
        ApplyLocalModSnapshot(await _workspace.ScanLocalModsAsync(CurrentSettings(), token));
        Changed(nameof(ProfileCountText));
        Status = T("SnapshotAndRestoredCopiesDeleted");
    });

    private async Task RestorePlaysetSnapshotCoreAsync(Guid id, CancellationToken token)
    {
        var settings = CurrentSettings();
        var profile = await _workspace.RestorePlaysetSnapshotAsync(settings, id, token);
        ApplyLocalModSnapshot(await _workspace.ScanLocalModsAsync(settings, token));
        PresentRestoredProfile(profile);
        Status = T("RestoredASeparateLocalProfileChooseApply");
    }

    private void PresentRestoredProfile(ModProfile profile)
    {
        _drafts.Remove(profile.Id);
        var existing = Profiles.FirstOrDefault(item => item.Id == profile.Id);
        if (existing is null) Profiles.Add(profile);
        else Profiles[Profiles.IndexOf(existing)] = profile;
        SelectedProfile = profile;
        Changed(nameof(ProfileCountText));
    }

    private void RefreshPlaysetSnapshotCommands()
    {
        ((AsyncCommand)CapturePlaysetSnapshotCommand).Refresh();
        ((AsyncCommand)RestorePlaysetSnapshotCommand).Refresh();
        ((AsyncCommand)DeletePlaysetSnapshotCommand).Refresh();
        Changed(nameof(CanDeletePlaysetSnapshot));
    }
}
