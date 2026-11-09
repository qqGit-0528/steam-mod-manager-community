namespace Ck3ModManager.Core;

public sealed record LocalModImportPreview(string SourcePath, string Name, string? ModVersion,
    string? SupportedGameVersion, ulong? WorkshopId, int FileCount, long Bytes, string ReviewStamp, string TargetPath);

public interface ILocalModInstaller
{
    Task<LocalModImportPreview> PreviewAsync(GameEnvironment environment, string sourcePath, CancellationToken cancellationToken);
    Task<string> InstallAsync(GameEnvironment environment, LocalModImportPreview preview, CancellationToken cancellationToken);
}
