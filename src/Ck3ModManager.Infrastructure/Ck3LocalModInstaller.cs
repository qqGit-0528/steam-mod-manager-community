using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class Ck3LocalModInstaller : ILocalModInstaller
{
    private const string Descriptor = "descriptor.mod";
    private const string LocalPrefix = "app_mm_import_";

    public Task<LocalModImportPreview> PreviewAsync(GameEnvironment environment, string sourcePath, CancellationToken cancellationToken) =>
        Task.Run(() => ReadSource(sourcePath, cancellationToken, (source, entries, directory) =>
            Analyze(environment, source, entries, directory, cancellationToken).Preview), cancellationToken);

    public Task<string> InstallAsync(GameEnvironment environment, LocalModImportPreview preview, CancellationToken cancellationToken) =>
        Task.Run(() => ReadSource(preview.SourcePath, cancellationToken, (source, entries, directory) =>
        {
            var input = Analyze(environment, source, entries, directory, cancellationToken);
            if (input.Preview != preview)
                throw new InvalidOperationException(T("TheModSourceOrInstallFolderChanged"));
            return Install(input, () =>
            {
                var current = directory ? PortableModFiles.ReadDirectory(source, cancellationToken) : entries;
                if (Analyze(environment, source, current, directory, cancellationToken).Preview != preview)
                    throw new InvalidOperationException(T("SourceFilesChangedDuringCopyingPreviewThe"));
            }, cancellationToken);
        }), cancellationToken);

    private sealed record ImportInput(LocalModImportPreview Preview, IReadOnlyList<PortableModFiles.Entry> Payload,
        IReadOnlyList<TextProperty> Properties, string DescriptorText);

    private static TResult ReadSource<TResult>(string sourcePath, CancellationToken token,
        Func<string, IReadOnlyList<PortableModFiles.Entry>, bool, TResult> read)
    {
        if (!Path.IsPathFullyQualified(sourcePath)) throw new InvalidDataException(T("ChooseALocalZIPOrFolder"));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var attributes = PortableModFiles.EnsureOrdinary(source);
        if ((attributes & FileAttributes.Directory) != 0)
            return read(source, PortableModFiles.ReadDirectory(source, token), true);
        if (!Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(T("ChooseAZIPWithOneCK3Mod"));
        using var input = PortableModFiles.OpenRead(source);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return read(source, PortableModFiles.ReadZip(archive, token), false);
    }

    private static ImportInput Analyze(GameEnvironment environment, string source, IReadOnlyList<PortableModFiles.Entry> entries,
        bool directory, CancellationToken token)
    {
        var candidates = entries.Where(entry => entry.Length is not null &&
            (entry.Path == Descriptor || entry.Path.EndsWith("/" + Descriptor, StringComparison.Ordinal) &&
             entry.Path.Count(character => character == '/') == 1)).Take(2).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException(T("TheZIPOrFolderMustContainExactly"));
        var descriptor = candidates[0];
        var text = PortableModFiles.ReadText(descriptor, token);
        var properties = TextDataParser.Read(text);
        var name = properties.Value("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl))
            throw new InvalidDataException(T("TheCK3DescriptorModIsMissingA"));
        var version = properties.Value("version");
        var supported = properties.Value("supported_version");
        var workshopId = ulong.TryParse(properties.Value("remote_file_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : (ulong?)null;

        var prefix = descriptor.Path[..^Descriptor.Length];
        var payload = entries.Where(entry => entry.Path.StartsWith(prefix, StringComparison.Ordinal) && entry.Path.Length > prefix.Length)
            .Select(entry => entry with { Path = entry.Path[prefix.Length..] }).ToArray();
        var files = payload.Where(entry => entry.Length is not null).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        if (files.Length < 2) throw new InvalidDataException(T("ThePackageContainsOnlyADescriptorAnd"));

        var modRoot = ModRoot(environment);
        if (directory)
        {
            var payloadRoot = Path.TrimEndingDirectorySeparator(Path.Combine(source, prefix));
            if (SteamGameLocator.PathComparer.Equals(payloadRoot, modRoot) || SteamGameLocator.IsWithin(payloadRoot, modRoot))
                throw new InvalidDataException(T("TheSourceFolderContainsTheInstallationFolder"));
        }

        var stamp = new StringBuilder(SnapshotFiles.TextHash(text));
        if (!directory) stamp.Append('|').Append(File.GetLastWriteTimeUtc(source).Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var entry in files)
        {
            token.ThrowIfCancellationRequested();
            stamp.Append('\n').Append(entry.Path).Append('|').Append(entry.Length!.Value.ToString(CultureInfo.InvariantCulture));
            if (directory)
            {
                var physical = Path.Combine(source, prefix + entry.Path);
                stamp.Append('|').Append(File.GetLastWriteTimeUtc(physical).Ticks.ToString(CultureInfo.InvariantCulture));
            }
        }
        var key = SnapshotFiles.TextHash($"{name.Normalize().ToUpperInvariant()}\n{version}\n{workshopId}");
        var target = Path.Combine(modRoot, LocalPrefix + key[..24].ToLowerInvariant());
        return new(new(source, name, version, supported, workshopId, files.Length, files.Sum(entry => entry.Length!.Value),
            SnapshotFiles.TextHash(stamp.ToString()), target), payload, properties, text);
    }

    private static string Install(ImportInput input, Action recheckSource, CancellationToken token)
    {
        var preview = input.Preview;
        var target = preview.TargetPath;
        var root = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(root);
        PortableModFiles.EnsureOrdinary(root);
        var lockPath = Path.Combine(root, ".app-mm-import.lock");
        SnapshotFiles.RejectLink(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var wrapper = target + ".mod";
        SnapshotFiles.RejectLink(target);
        SnapshotFiles.RejectLink(wrapper);
        if (File.Exists(target) || Directory.Exists(target) || File.Exists(wrapper) || Directory.Exists(wrapper))
            throw new IOException(T("AnImportWithThisNameAndVersion"));

        var stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(stage, "content");
        var stagedWrapper = Path.Combine(stage, "install.mod");
        var published = false;
        try
        {
            Directory.CreateDirectory(content);
            PortableModFiles.Extract(input.Payload, content, token);
            var stagedDescriptor = Path.Combine(content, Descriptor);
            if (TextDataParser.ReadSmallFile(stagedDescriptor) != input.DescriptorText)
                throw new InvalidDataException(T("TheModDescriptorChangedDuringInstallation"));
            File.WriteAllText(stagedDescriptor, LocalModDescriptor.Write(input.Properties, workshopId: preview.WorkshopId));
            File.WriteAllText(stagedWrapper, LocalModDescriptor.Write(input.Properties, "path", "mod/" + Path.GetFileName(target)));

            recheckSource();
            token.ThrowIfCancellationRequested();
            Directory.Move(content, target);
            published = true;
            File.Move(stagedWrapper, wrapper);
            return target;
        }
        catch
        {
            if (published) PortableModFiles.DeleteStage(target);
            throw;
        }
        finally { PortableModFiles.DeleteStage(stage); }
    }

    internal static bool IsManagedDirectory(string name) =>
        name.Length == LocalPrefix.Length + 24 && name.StartsWith(LocalPrefix, StringComparison.Ordinal) &&
        name[LocalPrefix.Length..].All(Uri.IsHexDigit);

    internal static string ModRoot(GameEnvironment environment)
    {
        if (!Path.IsPathFullyQualified(environment.UserDataDirectory))
            throw new InvalidOperationException(T("SetTheCK3UserDataFolderFirst"));
        var user = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataDirectory));
        var root = Path.Combine(user, "mod");
        if (!environment.LocalModDirectories.Any(path => SteamGameLocator.PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), root)))
            throw new InvalidOperationException(T("TheCK3LocalModFolderForThis"));
        SnapshotFiles.RejectLink(user);
        SnapshotFiles.RejectLink(root);
        return root;
    }
}
