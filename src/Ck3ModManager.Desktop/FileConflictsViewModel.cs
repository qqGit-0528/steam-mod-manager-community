using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record FileConflictRow(FileConflict Entry)
{
    public string Path => Entry.RelativePath;
    public string State => Entry.Kind switch
    {
        FileConflictKind.Identical => T("SameContent"),
        FileConflictKind.Different => T("DifferentContent"),
        _ => T("ComparisonUnavailable")
    };

    public string LoadOrder => string.Join(" → ", Entry.Mods.Select(mod => $"#{mod.Order} {mod.Name}"));
}

public sealed record DefinitionConflictRow(DefinitionConflict Entry)
{
    public string Key => Entry.Key;
    public string Sources => string.Join(Environment.NewLine,
        Entry.Sources.Select(source => $"#{source.Mod.Order} {source.Mod.Name} · {source.RelativePath}"));
}

public sealed record DirectoryReplacementRow(DirectoryReplacement Entry)
{
    public string Path => Entry.RelativePath;
    public string Detail => T("EarlierModPathsInThisDirectory", Entry.Mod.Order, Entry.Mod.Name, Entry.EarlierPaths);
}

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _fileConflictCheck;
    private FileConflictReport? _fileConflictReport;

    public bool SupportsFileConflictCheck => _workspace.HasFileConflictCheck;
    public bool HasFileConflictReport => _fileConflictReport is not null;
    public IReadOnlyList<FileConflictRow> FileConflictRows { get; private set; } = [];
    public IReadOnlyList<DefinitionConflictRow> DefinitionConflictRows { get; private set; } = [];
    public IReadOnlyList<DirectoryReplacementRow> DirectoryReplacementRows { get; private set; } = [];
    public bool HasDefinitionConflicts => DefinitionConflictRows.Count > 0;
    public bool HasDirectoryReplacements => DirectoryReplacementRows.Count > 0;
    public string DefinitionConflictSummary =>
        T("TraitDefinitionsDuplicateKeysScansTopLevel", DefinitionConflictRows.Count);
    public IReadOnlyList<string> FileConflictWarnings => _fileConflictReport?.Warnings ?? [];
    public bool HasFileConflictWarnings => FileConflictWarnings.Count > 0;
    public string FileConflictSummary
    {
        get
        {
            if (_fileConflictReport is not { } report) return "";
            var differences = report.Conflicts.Count(file => file.Kind == FileConflictKind.Different);
            var incomplete = report.Warnings.Count > 0 ? T("PartialCheck") : "";
            return T("ModsScannedSharedPathsDifferentContent", report.CheckedMods, report.Conflicts.Count, differences, incomplete);
        }
    }

    public ICommand CheckFileConflictsCommand { get; private set; } = null!;

    private void InitializeFileConflicts() =>
        CheckFileConflictsCommand = new AsyncCommand(CheckFileConflictsAsync, CanCheckFileConflicts);

    private bool CanCheckFileConflicts() =>
        SupportsFileConflictCheck && _localModMonitoringEnabled && !_disposed && IsProfiles && !IsBusy &&
        HasProfile && !HasProfileTransferPreview && ProfileMods.Any(row => row.Enabled);

    public async Task CheckFileConflictsAsync()
    {
        if (!CanCheckFileConflicts()) return;
        SetFileConflictReport(null);
        using var operation = new CancellationTokenSource();
        _fileConflictCheck = operation;
        try
        {
            await RunOperationAsync(T("CheckConflicts"), async token =>
            {
                var result = await _workspace.CheckFileConflictsAsync(CurrentSettings(), BuildGameProfile(), token);
                token.ThrowIfCancellationRequested();
                if (_disposed) return;
                SetFileConflictReport(result);
                Status = T("FileConflictCheckComplete");
            }, operation.Token);
        }
        finally
        {
            if (ReferenceEquals(_fileConflictCheck, operation)) _fileConflictCheck = null;
        }
    }

    private void InvalidateFileConflicts()
    {
        _fileConflictCheck?.Cancel();
        SetFileConflictReport(null);
    }

    private void SetFileConflictReport(FileConflictReport? report)
    {
        if (_fileConflictReport is null && report is null) return;
        _fileConflictReport = report;
        FileConflictRows = report?.Conflicts
            .OrderBy(file => file.Kind == FileConflictKind.Different ? 0 : file.Kind == FileConflictKind.Unknown ? 1 : 2)
            .Select(file => new FileConflictRow(file)).ToArray() ?? [];
        DefinitionConflictRows = report?.DefinitionConflicts.Select(entry => new DefinitionConflictRow(entry)).ToArray() ?? [];
        DirectoryReplacementRows = report?.DirectoryReplacements.Select(entry => new DirectoryReplacementRow(entry)).ToArray() ?? [];
        Changed(nameof(DefinitionConflictRows));
        Changed(nameof(DefinitionConflictSummary));
        Changed(nameof(HasDefinitionConflicts));
        Changed(nameof(DirectoryReplacementRows));
        Changed(nameof(HasDirectoryReplacements));
        Changed(nameof(HasFileConflictReport));
        Changed(nameof(FileConflictRows));
        Changed(nameof(FileConflictWarnings));
        Changed(nameof(HasFileConflictWarnings));
        Changed(nameof(FileConflictSummary));
    }
}
