namespace Ck3ModManager.Core;

public enum DiagnosticReadIssue { Unavailable, UnsafePath, NotText }

public sealed record DiagnosticIssue(string RelativePath, DiagnosticReadIssue Reason);

public sealed record DiagnosticLog(string RelativePath, DateTimeOffset UpdatedAt, string Tail, bool Truncated);

public sealed record DiagnosticGroup(string Directory, string? ReportName, DateTimeOffset? UpdatedAt, IReadOnlyList<DiagnosticLog> Logs);

public sealed record Ck3DiagnosticsReport(string UserDirectory, DiagnosticGroup? Session,
    IReadOnlyList<DiagnosticGroup> Crashes, IReadOnlyList<DiagnosticIssue> Issues);

public interface ICk3DiagnosticsReader
{
    Task<Ck3DiagnosticsReport> ReadAsync(GameEnvironment environment, CancellationToken cancellationToken);
}
