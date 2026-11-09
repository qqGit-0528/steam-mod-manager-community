namespace Ck3ModManager.Core;

public interface IWorkshopDependencySource
{
    Task<IReadOnlyList<ulong>?> ReadRequiredItemsAsync(uint appId, ulong itemId, CancellationToken cancellationToken = default);
}

public sealed record DependencyFinding(string Message, ulong? WorkshopId = null, string? SourceKey = null);
public sealed record ProfileDependencyReport(int CheckedItems, IReadOnlyList<DependencyFinding> Findings,
    IReadOnlyList<WorkshopItem> MissingWorkshopItems);
