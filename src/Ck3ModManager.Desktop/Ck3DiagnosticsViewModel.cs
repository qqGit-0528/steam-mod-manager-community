using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record DiagnosticGroupRow(DiagnosticGroup Group)
{
    public string Name => Group.ReportName ?? T("LatestSessionLogs");
    public string Timestamp => Group.UpdatedAt is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : T("TimeUnknown");
}

public sealed partial class MainWindowViewModel
{
    private Ck3DiagnosticsReport? _diagnosticsReport;
    private DiagnosticGroupRow? _selectedDiagnosticGroup;
    private DiagnosticLog? _selectedDiagnosticLog;
    private CancellationTokenSource? _diagnosticsRead;

    public bool SupportsCk3Diagnostics => _workspace.HasCk3Diagnostics;
    public bool IsDiagnostics => Section == "diagnostics" && SupportsCk3Diagnostics;
    public bool HasDiagnosticsReport => _diagnosticsReport is not null;
    public bool HasDiagnosticGroups => DiagnosticGroups.Count > 0;
    public bool HasDiagnosticSelection => SelectedDiagnosticGroup is not null;
    public bool HasDiagnosticLog => SelectedDiagnosticLog is not null;
    public bool HasDiagnosticIssues => _diagnosticsReport is { Issues.Count: > 0 };
    public IReadOnlyList<DiagnosticGroupRow> DiagnosticGroups { get; private set; } = [];
    public IReadOnlyList<DiagnosticLog> DiagnosticLogs => SelectedDiagnosticGroup?.Group.Logs ?? [];
    public string DiagnosticSummary => _diagnosticsReport is not { } report
        ? T("ReadRecentSessionLogsAndPastCrash")
        : T("SessionLogsPastCrashReports", report.Session?.Logs.Count ?? 0, report.Crashes.Count);
    public string DiagnosticDirectory => SelectedDiagnosticGroup?.Group.Directory ?? "";
    public string DiagnosticRoot => _diagnosticsReport?.UserDirectory ?? "";
    public string DiagnosticLogHeading => SelectedDiagnosticLog is not { } log ? "" :
        $"{log.RelativePath} · {log.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    public string DiagnosticLogTail => SelectedDiagnosticLog is not { } log ? "" :
        log.Tail.Length > 0 ? log.Tail : T("ThisLogIsEmpty");
    public bool IsDiagnosticLogTruncated => SelectedDiagnosticLog?.Truncated == true;
    public IReadOnlyList<string> DiagnosticIssues => _diagnosticsReport?.Issues.Select(issue => issue.Reason switch
    {
        DiagnosticReadIssue.UnsafePath => T("LinkOrSpecialFileSkipped", issue.RelativePath),
        DiagnosticReadIssue.NotText => T("CouldNotReadAsText", issue.RelativePath),
        _ => T("Unavailable", issue.RelativePath)
    }).ToArray() ?? [];

    public DiagnosticGroupRow? SelectedDiagnosticGroup
    {
        get => _selectedDiagnosticGroup;
        set
        {
            if (value is not null && !DiagnosticGroups.Contains(value)) return;
            if (!Set(ref _selectedDiagnosticGroup, value)) return;
            Changed(nameof(HasDiagnosticSelection));
            Changed(nameof(DiagnosticDirectory));
            Changed(nameof(DiagnosticLogs));
            SelectedDiagnosticLog = DiagnosticLogs.FirstOrDefault();
            RefreshDiagnosticCommands();
        }
    }

    public DiagnosticLog? SelectedDiagnosticLog
    {
        get => _selectedDiagnosticLog;
        set
        {
            if (value is not null && !DiagnosticLogs.Contains(value)) return;
            if (!Set(ref _selectedDiagnosticLog, value)) return;
            Changed(nameof(HasDiagnosticLog));
            Changed(nameof(DiagnosticLogHeading));
            Changed(nameof(DiagnosticLogTail));
            Changed(nameof(IsDiagnosticLogTruncated));
        }
    }

    public ICommand ReadDiagnosticsCommand { get; private set; } = null!;
    public ICommand OpenDiagnosticDirectoryCommand { get; private set; } = null!;

    private void InitializeDiagnostics()
    {
        ReadDiagnosticsCommand = new AsyncCommand(ReadDiagnosticsAsync, CanReadDiagnostics);
        OpenDiagnosticDirectoryCommand = new RelayCommand(_ =>
        {
            if (!CanReadDiagnostics() || SelectedDiagnosticGroup is not { } row) return;
            try { _navigator.OpenDirectory(row.Group.Directory); }
            catch (Exception error) { ErrorMessage = T("CouldNotOpenTheDiagnosticsFolder", error.Message); }
        }, () => CanReadDiagnostics() && HasDiagnosticSelection);
    }

    private bool CanReadDiagnostics() => IsDiagnostics && _localModMonitoringEnabled && !_disposed && !IsBusy;

    public async Task ReadDiagnosticsAsync()
    {
        if (!CanReadDiagnostics()) return;
        SetDiagnosticsReport(null);
        using var operation = new CancellationTokenSource();
        _diagnosticsRead = operation;
        try
        {
            await RunOperationAsync(T("ReadCK3Logs"), async token =>
            {
                var report = await _workspace.ReadCk3DiagnosticsAsync(CurrentSettings(), token);
                token.ThrowIfCancellationRequested();
                if (_disposed || !IsDiagnostics) return;
                SetDiagnosticsReport(report);
                Status = T("CK3LogsLoaded");
            }, operation.Token);
        }
        finally { if (ReferenceEquals(_diagnosticsRead, operation)) _diagnosticsRead = null; }
    }

    private void InvalidateDiagnostics()
    {
        _diagnosticsRead?.Cancel();
        SetDiagnosticsReport(null);
    }

    private void SetDiagnosticsReport(Ck3DiagnosticsReport? report)
    {
        if (report is null && _diagnosticsReport is null) return;
        _diagnosticsReport = report;
        var groups = new List<DiagnosticGroupRow>();
        if (report?.Session is { } session) groups.Add(new(session));
        if (report is not null) groups.AddRange(report.Crashes.Select(crash => new DiagnosticGroupRow(crash)));
        DiagnosticGroups = groups;
        Changed(nameof(HasDiagnosticsReport));
        Changed(nameof(HasDiagnosticGroups));
        Changed(nameof(DiagnosticRoot));
        Changed(nameof(DiagnosticGroups));
        Changed(nameof(DiagnosticSummary));
        Changed(nameof(DiagnosticIssues));
        Changed(nameof(HasDiagnosticIssues));
        SelectedDiagnosticGroup = groups.FirstOrDefault(group => group.Group.Logs.Count > 0) ?? groups.FirstOrDefault();
    }

    private void RefreshDiagnosticCommands()
    {
        ((AsyncCommand)ReadDiagnosticsCommand).Refresh();
        ((RelayCommand)OpenDiagnosticDirectoryCommand).Refresh();
    }
}
