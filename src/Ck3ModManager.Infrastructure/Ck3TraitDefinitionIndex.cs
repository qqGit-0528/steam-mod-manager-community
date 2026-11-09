using static Ck3ModManager.Core.AppText;
using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed partial class Ck3FileConflictScanner
{
    private const string TraitsDirectory = "common/traits/";
    private const int MaxScriptBytes = 1_048_576;

    private static bool IsTraitScript(string path) =>
        path.StartsWith(TraitsDirectory, StringComparison.Ordinal) &&
        path.IndexOf('/', TraitsDirectory.Length) < 0 &&
        path.EndsWith(".txt", StringComparison.Ordinal);

    private static void ApplyDirectoryReplacements(FileConflictMod mod, Dictionary<string, Payload> activeTraitFiles,
        Dictionary<string, Payload> earlierPaths, List<DirectoryReplacement> replacements,
        List<string> warnings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(mod.DescriptorPath) ||
            !Path.GetExtension(mod.DescriptorPath).Equals(".mod", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            ReadAttributes(mod.DescriptorPath);
            var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(mod.DescriptorPath));
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties.Where(property => property.Name == "replace_path"))
            {
                token.ThrowIfCancellationRequested();
                var relative = property.Scalar?.Replace('\\', '/').TrimEnd('/');
                if (relative is null || !IsSafeEntryPath(relative))
                {
                    warnings.Add(T("ContainsAnUnrecognizedReplacePathEntry", mod.Order, mod.Name));
                    continue;
                }
                if (!reported.Add(relative)) continue;

                var prefix = relative + "/";
                var affected = 0;
                foreach (var path in earlierPaths.Keys)
                {
                    token.ThrowIfCancellationRequested();
                    if (path.StartsWith(prefix, StringComparison.Ordinal)) affected++;
                }
                replacements.Add(new(relative, mod, affected));
                foreach (var path in activeTraitFiles.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    activeTraitFiles.Remove(path);
            }
        }
        catch (Exception error) when (IsScanError(error))
        { warnings.Add(T("CouldNotCheckDirectoryReplacement", mod.Order, mod.Name, error.Message)); }
    }

    private static IReadOnlyList<DefinitionConflict> FindDefinitionConflicts(
        Dictionary<string, Payload> traitFiles, List<string> warnings, CancellationToken token)
    {
        var sources = new Dictionary<string, List<DefinitionSource>>(StringComparer.Ordinal);
        foreach (var group in traitFiles.OrderBy(pair => pair.Value.Mod.Order)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal).GroupBy(pair => pair.Value.Mod))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var archive = group.First().Value.ArchiveIndex < 0 ? null : OpenArchive(group.Key.ContentPath);
                foreach (var (relative, payload) in group)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (payload.Length > MaxScriptBytes) throw new InvalidDataException(T("TheScriptExceeds1MiB"));
                        using var stream = OpenPayload(payload, archive);
                        var bytes = new byte[(int)payload.Length];
                        stream.ReadExactly(bytes);
                        if (stream.ReadByte() != -1) throw new InvalidDataException(T("TheScriptSizeChanged"));
                        var properties = TextDataParser.Read(Encoding.UTF8.GetString(bytes));
                        var keys = properties.Where(property => property.Block is not null && IsTraitKey(property.Name))
                            .Select(property => property.Name).Distinct(StringComparer.Ordinal);
                        foreach (var key in keys)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!sources.TryGetValue(key, out var entries)) sources.Add(key, entries = []);
                            entries.Add(new(relative, payload.Mod));
                        }
                    }
                    catch (Exception error) when (IsScanError(error))
                    { warnings.Add(T("CouldNotParse", payload.Mod.Order, payload.Mod.Name, relative, error.Message)); }
                }
            }
            catch (Exception error) when (IsScanError(error))
            { warnings.Add(T("CouldNotReadDefinitionsInTheZIP", group.Key.Order, group.Key.Name, error.Message)); }
        }

        return sources.Where(pair => pair.Value.Count > 1)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DefinitionConflict(pair.Key, pair.Value)).ToArray();
    }

    private static bool IsTraitKey(string key) =>
        key.Length > 0 && key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
