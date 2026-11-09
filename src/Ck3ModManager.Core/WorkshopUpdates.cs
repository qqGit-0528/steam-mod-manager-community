namespace Ck3ModManager.Core;

public enum WorkshopUpdateState { Unknown, NeedsUpdate, Processing, NoPendingUpdate }

public sealed record WorkshopStatusCheck(IReadOnlyDictionary<ulong, WorkshopUpdateState> States, string? Note = null);

public interface IWorkshopStatusSource
{
    Task<WorkshopStatusCheck> CheckAsync(GameEnvironment environment, IReadOnlyList<ulong> itemIds,
        CancellationToken cancellationToken = default);
}

public sealed record WorkshopUpdateEntry(ulong ItemId, string Name, WorkshopUpdateState State);
public sealed record WorkshopUpdateReport(IReadOnlyList<WorkshopUpdateEntry> Entries, string? Note);
