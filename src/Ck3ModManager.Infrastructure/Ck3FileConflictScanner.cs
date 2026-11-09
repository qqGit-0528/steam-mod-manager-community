using static Ck3ModManager.Core.AppText;
using System.IO.Compression;
using System.Security.Cryptography;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed partial class Ck3FileConflictScanner : IFileConflictScanner
{
    private sealed record Payload(FileConflictMod Mod, string Path, long Length, int ArchiveIndex = -1);
    private const int MaxFiles = 500_000;
    private const long MaxComparisonBytes = 1L << 30;

    public Task<FileConflictReport> CheckAsync(IReadOnlyList<FileConflictMod> mods, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(mods, cancellationToken), cancellationToken);

    private static FileConflictReport Check(IReadOnlyList<FileConflictMod> mods, CancellationToken token)
    {
        var firstByPath = new Dictionary<string, Payload>(StringComparer.Ordinal);
        var collisions = new Dictionary<string, List<Payload>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var activeTraitFiles = new Dictionary<string, Payload>(StringComparer.Ordinal);
        var replacements = new List<DirectoryReplacement>();
        var checkedMods = 0;
        var totalFiles = 0;
        foreach (var mod in mods.OrderBy(mod => mod.Order))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var files = ReadPayloads(mod, token);
                if (files.Count > MaxFiles - totalFiles)
                {
                    warnings.Add(T("SkippedBecauseTheFileCountLimitWas", mod.Order, mod.Name));
                    continue;
                }
                ApplyDirectoryReplacements(mod, activeTraitFiles, firstByPath, replacements, warnings, token);
                foreach (var (path, payload) in files)
                {
                    if (IsTraitScript(path)) activeTraitFiles[path] = payload;
                    if (firstByPath.TryAdd(path, payload)) continue;
                    if (!collisions.TryGetValue(path, out var entries))
                        collisions.Add(path, entries = [firstByPath[path]]);
                    entries.Add(payload);
                }
                totalFiles += files.Count;
                checkedMods++;
            }
            catch (Exception error) when (IsScanError(error))
            { warnings.Add(T("CouldNotBeListed", mod.Order, mod.Name, error.Message)); }
        }

        var equalLengths = collisions.Values.Where(entries => entries.All(file => file.Length == entries[0].Length));
        var hashes = ReadHashes(equalLengths.SelectMany(entries => entries), warnings, token);
        var results = collisions.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
        {
            var first = entry.Value[0];
            var differentLength = entry.Value.Any(file => file.Length != first.Length);
            var values = differentLength ? [] : entry.Value.Select(file => hashes.GetValueOrDefault(file)).ToArray();
            var kind = differentLength || values.OfType<string>().Distinct().Take(2).Count() > 1
                ? FileConflictKind.Different
                : values.Any(hash => hash is null) ? FileConflictKind.Unknown : FileConflictKind.Identical;
            return new FileConflict(entry.Key, kind, entry.Value.Select(file => file.Mod).ToArray());
        }).ToArray();

        var definitions = FindDefinitionConflicts(activeTraitFiles, warnings, token);
        token.ThrowIfCancellationRequested();
        return new(checkedMods, results, warnings)
        {
            DefinitionConflicts = definitions,
            DirectoryReplacements = replacements
        };
    }

    private static Dictionary<string, Payload> ReadPayloads(FileConflictMod mod, CancellationToken token)
    {
        var files = new Dictionary<string, Payload>(StringComparer.Ordinal);
        if (!Path.IsPathFullyQualified(mod.ContentPath))
            throw new InvalidDataException(T("TheInstallationPathIsInvalid"));
        var attributes = ReadAttributes(mod.ContentPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            var directories = new Stack<string>();
            directories.Push(mod.ContentPath);
            while (directories.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(mod.ContentPath, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (!IsPayload(relative)) continue;
                    if ((ReadAttributes(path) & FileAttributes.Directory) != 0) directories.Push(path);
                    else Add(relative, new(mod, path, new FileInfo(path).Length));
                }
            }
        }
        else
        {
            using var archive = ZipFile.OpenRead(mod.ContentPath);
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.Entries[index];
                var relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (!IsSafeEntryPath(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException(T("TheZIPContainsAnInvalidPathOr"));
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || !IsPayload(relative)) continue;
                Add(relative, new(mod, entry.FullName, entry.Length, index));
            }
        }
        return files;

        void Add(string relative, Payload payload)
        {
            if (files.Count >= MaxFiles) throw new InvalidDataException(T("ThePerModFileCountLimitWas"));
            if (!files.TryAdd(relative, payload)) throw new InvalidDataException(T("DuplicateFilePath", relative));
        }
    }

    private static Dictionary<Payload, string> ReadHashes(IEnumerable<Payload> files, List<string> warnings, CancellationToken token)
    {
        var hashes = new Dictionary<Payload, string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        foreach (var group in files.GroupBy(file => file.Mod))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var first = group.First();
                using var archive = first.ArchiveIndex < 0 ? null : OpenArchive(group.Key.ContentPath);
                foreach (var file in group)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (file.Length > MaxComparisonBytes) throw new InvalidDataException(T("TheFileExceedsThe1GiBComparison"));
                        using var stream = OpenPayload(file, archive);
                        long total = 0;
                        int read;
                        while ((read = stream.Read(buffer)) != 0)
                        {
                            token.ThrowIfCancellationRequested();
                            total += read;
                            if (total > file.Length) break;
                            hash.AppendData(buffer.AsSpan(0, read));
                        }
                        if (total != file.Length) throw new InvalidDataException(T("TheFileSizeChangedDuringTheCheck"));
                        hashes.Add(file, Convert.ToHexString(hash.GetHashAndReset()));
                    }
                    catch (Exception error) when (IsScanError(error))
                    {
                        hash.GetHashAndReset();
                        warnings.Add(T("CouldNotCompare", file.Mod.Order, file.Mod.Name, file.Path, error.Message));
                    }
                }
            }
            catch (Exception error) when (IsScanError(error))
            { warnings.Add(T("CouldNotReopenTheZIP", group.Key.Order, group.Key.Name, error.Message)); }
        }
        return hashes;
    }

    private static ZipArchive OpenArchive(string path)
    {
        ReadAttributes(path);
        return ZipFile.OpenRead(path);
    }

    private static Stream OpenPayload(Payload file, ZipArchive? archive)
    {
        if (archive is null)
        {
            ReadAttributes(file.Path);
            return File.OpenRead(file.Path);
        }

        if (file.ArchiveIndex >= archive.Entries.Count ||
            archive.Entries[file.ArchiveIndex] is not { } entry ||
            entry.FullName != file.Path || entry.Length != file.Length)
            throw new InvalidDataException(T("TheZIPContentsChangedDuringTheCheck"));
        return entry.Open();
    }

    private static FileAttributes ReadAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(T("SkippedSymbolicLinkOrReparsePoint", path));
        return attributes;
    }

    private static bool IsSafeEntryPath(string path) =>
        !path.Contains(':') && !path.Contains('\0') &&
        path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static bool IsPayload(string path) =>
        path is not "descriptor.mod" and not ".metadata" and not "__MACOSX" &&
        !path.StartsWith(".metadata/", StringComparison.Ordinal) && !path.StartsWith("__MACOSX/", StringComparison.Ordinal) &&
        !Path.GetFileName(path).Equals(".DS_Store", StringComparison.Ordinal);

    private static bool IsScanError(Exception error) =>
        error is InvalidDataException || SteamGameLocator.IsReadableError(error);
}
