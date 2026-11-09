using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed partial class MainWindowViewModel
{
    private Ck3SaveSelection? _selectedCk3Save;
    private PlaysetSnapshotRow? _saveBindingSnapshot;
    private Ck3SaveCheck? _saveCheck;

    public bool SupportsSaveBindings => _workspace.HasSaveBindings;
    public bool HasSelectedCk3Save => _selectedCk3Save is not null;
    public string SaveFileName => _selectedCk3Save is { } save ? Path.GetFileName(save.Path) : T("NoSaveSelected");
    public string SaveMetadataText => _selectedCk3Save is { } save
        ? T("SaveVersionModEntries", save.Metadata.GameVersion ?? T("Unknown"), save.Metadata.ModDescriptors?.Count.ToString() ?? T("Unknown")) : "";
    public string SaveModDescriptors => _selectedCk3Save?.Metadata.ModDescriptors is { Count: > 0 } descriptors
        ? string.Join(Environment.NewLine, descriptors)
        : _selectedCk3Save?.Metadata.ModDescriptors is null ? T("TheSaveHasNoReadableModList") : T("TheSaveSModListIsEmpty");
    public string SaveBindingText
    {
        get
        {
            if (_selectedCk3Save is not { } save) return "";
            if (save.Stamp is null) return T("TheSaveMovedOrIsUnavailableSelect2");
            if (save.NeedsRebind) return T("TheSaveChangedConfirmItsBindingAgain2");
            if (save.Binding is not { } binding) return T("SelectASnapshotAndBindItTo");
            var snapshot = PlaysetSnapshots.FirstOrDefault(row => row.Snapshot.Id == binding.SnapshotId);
            return snapshot is null ? T("TheBoundSnapshotIsUnavailableBindAnother") : T("BoundTo", snapshot.Label);
        }
    }

    public PlaysetSnapshotRow? SaveBindingSnapshot
    {
        get => _saveBindingSnapshot;
        set { if (Set(ref _saveBindingSnapshot, value)) RefreshSaveBindingCommands(); }
    }
    public IReadOnlyList<string> SaveCheckMessages => _saveCheck?.Differences is { Count: > 0 } differences ? differences
        : _saveCheck is null ? [] : [T("CK3VersionEnabledModsFilesAndLoad")];

    public ICommand BindCk3SaveCommand { get; private set; } = null!;
    public ICommand CheckCk3SaveCommand { get; private set; } = null!;
    public ICommand RestoreBoundSaveCommand { get; private set; } = null!;

    private void InitializeSaveBindings()
    {
        BindCk3SaveCommand = new AsyncCommand(BindCk3SaveAsync,
            () => !IsBusy && _selectedCk3Save?.Stamp is not null && SaveBindingSnapshot is not null);
        CheckCk3SaveCommand = new AsyncCommand(CheckCk3SaveAsync,
            () => !IsBusy && HasProfile && _selectedCk3Save?.Binding is not null);
        RestoreBoundSaveCommand = new AsyncCommand(RestoreBoundSaveAsync,
            () => !IsBusy && _saveCheck?.CanRestore == true);
    }

    public Task SelectCk3SaveAsync(string path) => RunOperationAsync(T("ReadCK3Save"), async token =>
    {
        SetSelectedCk3Save(await _workspace.ReadSaveAsync(path, token));
        SaveBindingSnapshot = PlaysetSnapshots.FirstOrDefault(row => row.Snapshot.Id == _selectedCk3Save?.Binding?.SnapshotId)
            ?? SelectedPlaysetSnapshot;
        Status = T("SaveInformationLoaded");
    });

    private Task BindCk3SaveAsync() => RunOperationAsync(T("BindSaveToSnapshot"), async token =>
    {
        if (_selectedCk3Save is not { } save || SaveBindingSnapshot is not { } snapshot) return;
        SetSelectedCk3Save(await _workspace.BindSaveAsync(save, snapshot.Snapshot.Id, token));
        Status = T("LocalSaveBindingStored");
    });

    private Task CheckCk3SaveAsync() => RunOperationAsync(T("CheckSaveEnvironment"), async token =>
    {
        if (_selectedCk3Save is not { } save || SelectedProfile is not { } selected) return;
        ClearSaveCheck();
        var settings = CurrentSettings();
        var profile = selected with { Mods = ProfileMods.Select(row => row.Mod).ToArray() };
        var result = await _workspace.CheckSaveAsync(settings, save.Path, profile, token);
        token.ThrowIfCancellationRequested();
        if (SelectedProfile?.Id != selected.Id || CurrentSettings() != settings ||
            !ProfileMods.Select(row => row.Mod).SequenceEqual(profile.Mods)) return;
        SetSelectedCk3Save(result.Save);
        _saveCheck = result;
        Changed(nameof(SaveCheckMessages));
        RefreshSaveBindingCommands();
        Status = result.Differences.Count == 0 ? T("SaveEnvironmentMatchesTheSnapshot") : T("SaveEnvironmentCheckedReviewTheDifferences");
    });

    private Task RestoreBoundSaveAsync() => RunOperationAsync(T("RestoreBoundSnapshot"), async token =>
    {
        if (_saveCheck is not { CanRestore: true, Save.Binding: { } binding } check) return;
        var current = await _workspace.ReadSaveAsync(check.Save.Path, token);
        if (current.Stamp != check.Save.Stamp || current.Binding != binding)
        {
            SetSelectedCk3Save(current);
            throw new InvalidOperationException(T("TheSaveOrBindingChangedCheckAgain"));
        }
        await RestorePlaysetSnapshotCoreAsync(binding.SnapshotId, token);
    });

    private void SetSelectedCk3Save(Ck3SaveSelection value)
    {
        _selectedCk3Save = value;
        ClearSaveCheck();
        Changed(nameof(HasSelectedCk3Save));
        Changed(nameof(SaveFileName));
        Changed(nameof(SaveMetadataText));
        Changed(nameof(SaveModDescriptors));
        Changed(nameof(SaveBindingText));
        RefreshSaveBindingCommands();
    }

    private void ClearSaveCheck()
    {
        if (_saveCheck is null) return;
        _saveCheck = null;
        Changed(nameof(SaveCheckMessages));
        RefreshSaveBindingCommands();
    }

    private void RefreshSaveBindingCommands()
    {
        ((AsyncCommand)BindCk3SaveCommand).Refresh();
        ((AsyncCommand)CheckCk3SaveCommand).Refresh();
        ((AsyncCommand)RestoreBoundSaveCommand).Refresh();
    }
}
