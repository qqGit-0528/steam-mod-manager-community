namespace Ck3ModManager.Core;

public static class WorkshopDownloadHistory
{
    public const int RecentLimit = 100;
}

public enum WorkshopDownloadState
{
    Queued, Resolving, AwaitingAuthentication, Downloading, Verifying, Installing,
    Completed, Failed, Cancelled, Interrupted
}

public sealed record WorkshopContentFile(string RelativePath, long Size, string Sha1);

public sealed record WorkshopContentPlan(uint AppId, ulong PublishedFileId, string Revision,
    IReadOnlyList<WorkshopContentFile> Files, object? Transport = null)
{
    public long TotalBytes => Files.Aggregate(0L, (sum, file) => checked(sum + file.Size));
}

public sealed record WorkshopTransferProgress(WorkshopDownloadState State, long Bytes = 0,
    long? TotalBytes = null, string? Message = null, string? AuthenticationUrl = null, long TransferredBytes = 0);

public sealed record WorkshopDownloadSnapshot(Guid TaskId, uint AppId, ulong PublishedFileId,
    string Title, WorkshopDownloadState State, long Bytes, long? TotalBytes,
    DateTimeOffset UpdatedAt, string? Revision = null, string? InstalledPath = null,
    string? Message = null, string? AuthenticationUrl = null, double BytesPerSecond = 0)
{
    public WorkshopDownloadOwner Owner { get; init; } = WorkshopDownloadOwner.Application;
    public long? InstalledSizeBytes { get; init; }
    public bool IsUpToDate { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> WorkshopGameVersions { get; init; } = [];
    public bool IsTerminal => State is WorkshopDownloadState.Completed or WorkshopDownloadState.Failed
        or WorkshopDownloadState.Cancelled or WorkshopDownloadState.Interrupted;
}

public sealed record WorkshopInstallResult(string ContentPath, string DescriptorPath, bool Reused);

public interface IWorkshopContentProvider
{
    Task<WorkshopContentPlan> ResolveAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken);
    Task DownloadAsync(WorkshopContentPlan plan, string stagingDirectory, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken);
}

public interface IWorkshopDownloadStore
{
    Task<IDisposable> AcquireAsync(uint appId, ulong itemId, CancellationToken cancellationToken);
    string GetStagingDirectory(uint appId, ulong itemId, string revision);
    Task<WorkshopInstallResult?> FindVerifiedAsync(WorkshopContentPlan plan, string userDataDirectory, CancellationToken cancellationToken);
    Task<WorkshopInstallResult> InstallAsync(WorkshopContentPlan plan, string stagingDirectory, string userDataDirectory, CancellationToken cancellationToken);
    Task SaveTaskAsync(WorkshopDownloadSnapshot snapshot, CancellationToken cancellationToken);
    Task DeleteTaskAsync(Guid taskId, uint appId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkshopDownloadSnapshot>> ReadTasksAsync(CancellationToken cancellationToken);
}
