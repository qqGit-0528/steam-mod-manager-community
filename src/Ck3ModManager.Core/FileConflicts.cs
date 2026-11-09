namespace Ck3ModManager.Core;

public sealed record FileConflictMod(int Order, string Name, string ContentPath)
{
    public string? DescriptorPath { get; init; }
}

public enum FileConflictKind { Identical, Different, Unknown }

public sealed record FileConflict(string RelativePath, FileConflictKind Kind, IReadOnlyList<FileConflictMod> Mods);

public sealed record DefinitionSource(string RelativePath, FileConflictMod Mod);
public sealed record DefinitionConflict(string Key, IReadOnlyList<DefinitionSource> Sources);
public sealed record DirectoryReplacement(string RelativePath, FileConflictMod Mod, int EarlierPaths);

public sealed record FileConflictReport(int CheckedMods, IReadOnlyList<FileConflict> Conflicts, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<DefinitionConflict> DefinitionConflicts { get; init; } = [];
    public IReadOnlyList<DirectoryReplacement> DirectoryReplacements { get; init; } = [];
}

public interface IFileConflictScanner
{
    Task<FileConflictReport> CheckAsync(IReadOnlyList<FileConflictMod> mods, CancellationToken cancellationToken = default);
}
