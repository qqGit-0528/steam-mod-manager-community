using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record WorkshopUpdateRow(WorkshopUpdateEntry Entry)
{
    public string Name => Entry.Name;
    public string Status => Entry.State switch
    {
        WorkshopUpdateState.NeedsUpdate => T("UpdatePending"),
        WorkshopUpdateState.Processing => T("SteamProcessing"),
        WorkshopUpdateState.NoPendingUpdate => T("NoPendingUpdate"),
        _ => T("StatusUnknown")
    };
}

public sealed partial class MainWindowViewModel
{
    private WorkshopUpdateReport? _workshopUpdateReport;

    public bool SupportsWorkshopUpdateCheck => _workspace.HasWorkshopStatus;
    public bool HasWorkshopUpdateReport => _workshopUpdateReport is not null;
    public IReadOnlyList<WorkshopUpdateRow> WorkshopUpdateRows { get; private set; } = [];
    public string WorkshopUpdateNote => _workshopUpdateReport?.Note ?? "";
    public bool HasWorkshopUpdateNote => !string.IsNullOrEmpty(WorkshopUpdateNote);
    public ICommand CheckWorkshopUpdatesCommand { get; private set; } = null!;

    private void InitializeWorkshopUpdates() =>
        CheckWorkshopUpdatesCommand = new AsyncCommand(CheckWorkshopUpdatesAsync,
            () => SupportsWorkshopUpdateCheck && !IsBusy && HasProfile);

    public Task CheckWorkshopUpdatesAsync() => RunOperationAsync(T("CheckUpdates"), async token =>
    {
        if (SelectedProfile is not { } selected) return;
        SetWorkshopUpdateReport(null);
        var settings = CurrentSettings();
        var profile = selected with { Mods = ProfileMods.Select(row => row.Mod).ToArray() };
        var result = await _workspace.CheckWorkshopUpdatesAsync(settings, profile, token);
        token.ThrowIfCancellationRequested();
        if (SelectedProfile?.Id != selected.Id || CurrentSettings() != settings ||
            !ProfileMods.Select(row => row.Mod).SequenceEqual(profile.Mods)) return;

        SetWorkshopUpdateReport(result);
        var updates = result.Entries.Count(row => row.State == WorkshopUpdateState.NeedsUpdate);
        var unknown = result.Entries.Count(row => row.State == WorkshopUpdateState.Unknown);
        Status = unknown == 0
            ? T("WorkshopModsCheckedUpdatesPending", result.Entries.Count, updates)
            : T("WorkshopModsCheckedUpdatesPendingUnknown", result.Entries.Count, updates, unknown);
    });

    private void SetWorkshopUpdateReport(WorkshopUpdateReport? report)
    {
        if (_workshopUpdateReport is null && report is null) return;
        _workshopUpdateReport = report;
        WorkshopUpdateRows = report?.Entries.Select(entry => new WorkshopUpdateRow(entry)).ToArray() ?? [];
        Changed(nameof(WorkshopUpdateRows));
        Changed(nameof(WorkshopUpdateNote));
        Changed(nameof(HasWorkshopUpdateNote));
        Changed(nameof(HasWorkshopUpdateReport));
    }
}
