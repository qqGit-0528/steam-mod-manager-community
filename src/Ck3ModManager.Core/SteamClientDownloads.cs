namespace Ck3ModManager.Core;

public enum WorkshopDownloadOwner { Application, SteamClient }

public sealed record SteamClientInstallation(string ContentPath, long SizeOnDisk, bool Reused,
    long DownloadedBytes = 0, long? DownloadTotalBytes = null, string? Revision = null);

/// <summary>请求本机 Steam 托管下载；所有进度及完成状态来自客户端。</summary>
public interface ISteamClientWorkshopDownloader
{
    Task<SteamClientInstallation> DownloadAsync(WorkshopItem item,
        IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken);
}

public interface ISteamClientTaskHistoryStore
{
    /// <summary>调用方持有对应游戏和工坊物品的下载锁。</summary>
    Task<WorkshopDownloadSnapshot?> FindSteamItemTaskAsync(uint appId, ulong itemId, Guid? knownTaskId,
        CancellationToken cancellationToken);
    Task DeleteSteamItemTaskIfUnchangedAsync(WorkshopDownloadSnapshot snapshot, CancellationToken cancellationToken);
    Task<bool> DeleteSteamItemTasksAsync(uint appId, ulong itemId, CancellationToken cancellationToken,
        Guid? taskId = null, DateTimeOffset? expectedUpdatedAt = null);
}
