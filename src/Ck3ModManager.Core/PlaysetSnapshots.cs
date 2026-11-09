namespace Ck3ModManager.Core;

public sealed record PlaysetSnapshot(Guid Id, string Name, string GameVersion, DateTimeOffset CreatedAt, int EnabledCount);

public sealed record PlaysetSnapshotComparison(string GameVersion, IReadOnlyList<string> Differences);

public interface IPlaysetSnapshotStore
{
    Task<IReadOnlyList<PlaysetSnapshot>> ListAsync(CancellationToken cancellationToken = default);
    Task<PlaysetSnapshot> CaptureAsync(GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default);
    Task<PlaysetSnapshotComparison> CompareAsync(GameEnvironment environment, Guid id, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default);
    Task ValidateGameVersionAsync(GameEnvironment environment, Guid profileId, CancellationToken cancellationToken = default);
    Task<ModProfile> RestoreAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default);
    /// <summary>删除快照本体，并清理该快照恢复出的本地副本与描述文件。</summary>
    Task DeleteAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default);
}

public sealed record PlaysetPackageMod(string Name, bool Enabled, string? ModVersion);
public sealed record PlaysetPackagePreview(string SourcePath, string Name, string GameVersion,
    IReadOnlyList<PlaysetPackageMod> Mods, long Bytes, string ManifestHash);

public interface IPlaysetPackageStore
{
    Task ExportPackageAsync(GameEnvironment environment, ModProfile profile, IReadOnlyList<InstalledMod> installed,
        string destinationPath, CancellationToken cancellationToken);
    Task<PlaysetPackagePreview> PreviewPackageAsync(string sourcePath, CancellationToken cancellationToken);
    Task<ModProfile> ImportPackageAsync(GameEnvironment environment, PlaysetPackagePreview preview, CancellationToken cancellationToken);
    Task RemoveImportedPackageAsync(GameEnvironment environment, Guid profileId);
}
