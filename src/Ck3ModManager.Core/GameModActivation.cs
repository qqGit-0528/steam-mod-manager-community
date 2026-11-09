namespace Ck3ModManager.Core;

public sealed record GameProfileApplyResult(string PlaysetName, int EnabledCount, string BackupPath);

public interface IGameModActivator
{
    Task<ModProfile?> ReadAsync(GameEnvironment environment, IReadOnlyList<InstalledMod> installed,
        CancellationToken cancellationToken = default);
    Task<GameProfileApplyResult> ApplyAsync(GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default);
}
