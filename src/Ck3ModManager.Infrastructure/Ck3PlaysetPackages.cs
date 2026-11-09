using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed partial class Ck3PlaysetSnapshotStore
{
    private static readonly JsonSerializerOptions PackageJson = new(JsonFiles.Options)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public Task ExportPackageAsync(GameEnvironment environment, ModProfile profile, IReadOnlyList<InstalledMod> installed,
        string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() => ExportPackage(environment, profile, installed, destinationPath, cancellationToken), cancellationToken);

    public Task<PlaysetPackagePreview> PreviewPackageAsync(string sourcePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var source = PackagePath(sourcePath);
            using var input = PortableModFiles.OpenRead(source);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            return ReadPackage(source, archive, cancellationToken).Preview;
        }, cancellationToken);

    public Task<ModProfile> ImportPackageAsync(GameEnvironment environment, PlaysetPackagePreview preview, CancellationToken cancellationToken) =>
        Task.Run(() => ImportPackage(environment, preview, cancellationToken), cancellationToken);

    public Task RemoveImportedPackageAsync(GameEnvironment environment, Guid profileId) => Task.Run(() =>
    {
        var manifest = ReadImportedManifest(environment, profileId);
        if (manifest is null) return;
        var root = Ck3LocalModInstaller.ModRoot(environment);
        var name = LocalPrefix + profileId.ToString("N");
        for (var index = 0; index < manifest.Mods.Length; index++)
        {
            if (!manifest.Mods[index].Enabled) continue;
            var path = Path.Combine(root, name + "_" + SlotName(index) + ".mod");
            SnapshotFiles.RejectLink(path);
            File.Delete(path);
        }
        Directory.Delete(Path.Combine(root, name), recursive: true);
    });

    private void ExportPackage(GameEnvironment environment, ModProfile profile, IReadOnlyList<InstalledMod> installed,
        string destinationPath, CancellationToken token)
    {
        var destination = PackagePath(destinationPath);
        SnapshotFiles.RejectLink(destination);
        var enabled = profile.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId).ToHashSet(StringComparer.Ordinal);
        if (installed.Any(mod => enabled.Contains(mod.Id) && (SteamGameLocator.PathComparer.Equals(mod.ContentPath, destination) ||
            SteamGameLocator.IsWithin(mod.ContentPath, destination))))
            throw new InvalidOperationException(T("SaveThePackOutsideTheFoldersOf"));

        var id = Guid.NewGuid();
        SnapshotFiles.RejectLink(_root);
        Directory.CreateDirectory(_root);
        var stage = Path.Combine(_root, ".stage-package-" + id.ToString("N"));
        var temporary = destination + "." + id.ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(stage);
            var manifest = CaptureInto(stage, id, environment, profile, installed, token);
            ValidateNestedArchives(stage, manifest, token);
            var entries = PortableModFiles.ReadDirectory(stage, token);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var buffer = new byte[128 * 1024];
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.Length is null) archive.CreateEntry(entry.Path + "/");
                        else
                        {
                            var compression = entry.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                            using var target = archive.CreateEntry(entry.Path, compression).Open();
                            PortableModFiles.Copy(entry, target, buffer, token);
                        }
                    }
                }
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            SnapshotFiles.RejectLink(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            DeleteStage(stage);
        }
    }

    private static ModProfile ImportPackage(GameEnvironment environment, PlaysetPackagePreview preview, CancellationToken token)
    {
        var source = PackagePath(preview.SourcePath);
        var root = Ck3LocalModInstaller.ModRoot(environment);
        using var input = PortableModFiles.OpenRead(source);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var (manifest, actual, entries) = ReadPackage(source, archive, token);
        if (actual.ManifestHash != preview.ManifestHash || actual.Bytes != preview.Bytes)
            throw new InvalidOperationException(T("ThePackChangedPreviewItAgain"));
        CheckVersion(manifest, GameVersion(environment));

        Directory.CreateDirectory(root);
        PortableModFiles.EnsureOrdinary(root);
        var stage = Path.Combine(root, ".stage-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            PortableModFiles.Extract(entries, stage, token);
            ValidateNestedArchives(stage, manifest, token);
            var local = manifest with { Id = Guid.NewGuid() };
            WriteManifest(stage, local);
            return Restore(environment, local, stage, adoptStagedPackage: true, token);
        }
        finally { DeleteStage(stage); }
    }

    private static (Manifest Manifest, PlaysetPackagePreview Preview, IReadOnlyList<PortableModFiles.Entry> Entries)
        ReadPackage(string source, ZipArchive archive, CancellationToken token)
    {
        var entries = PortableModFiles.ReadZip(archive, token);
        var manifestEntry = entries.FirstOrDefault(entry => entry.Path == ManifestFile && entry.Length is not null)
            ?? throw new InvalidDataException(T("ThePackIsMissingItsManifest"));
        var json = PortableModFiles.ReadText(manifestEntry, token);
        var manifest = ParseManifest(json, id: null, PackageJson);
        ValidatePackageLayout(entries, manifest, token);
        var mods = manifest.Mods.Select(mod => new PlaysetPackageMod(mod.Name, mod.Enabled, mod.ModVersion)).ToArray();
        return (manifest, new(source, manifest.Name, manifest.GameVersion, mods, entries.Sum(entry => entry.Length ?? 0),
            SnapshotFiles.TextHash(json)), entries);
    }

    private static void ValidatePackageLayout(IReadOnlyList<PortableModFiles.Entry> entries, Manifest manifest, CancellationToken token)
    {
        var hasDescriptor = new bool[manifest.Mods.Length];
        var hasPayload = new bool[manifest.Mods.Length];
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Path == ManifestFile) continue;
            var separator = entry.Path.IndexOf('/');
            var slot = separator < 0 ? entry.Path : entry.Path[..separator];
            if (slot.Length != 4 || !int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index >= manifest.Mods.Length || !manifest.Mods[index].Enabled)
                throw new InvalidDataException(T("ThePackContainsAPathOutsideThe", entry.Path));
            if (separator < 0 && entry.Length is null) continue;
            var relative = separator < 0 ? "" : entry.Path[(separator + 1)..];
            if (relative == "descriptor.mod" && entry.Length is not null) hasDescriptor[index] = true;
            else if (manifest.Mods[index].Archive ? relative == "content.zip" && entry.Length is not null :
                relative == "content" && entry.Length is null || relative.StartsWith("content/", StringComparison.Ordinal))
                hasPayload[index] = true;
            else throw new InvalidDataException(T("ThePackContainsAnUnrecognizedEntry", entry.Path));
        }
        for (var index = 0; index < manifest.Mods.Length; index++)
            if (manifest.Mods[index].Enabled && (!hasDescriptor[index] || !hasPayload[index]))
                throw new InvalidDataException(T("CannotReproduceThisModBecauseThePack", manifest.Mods[index].Name));
    }

    private static void ValidateNestedArchives(string root, Manifest manifest, CancellationToken token)
    {
        long bytes = 0;
        var count = 0;
        for (var index = 0; index < manifest.Mods.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (manifest.Mods[index] is not { Enabled: true, Archive: true }) continue;
            using var stream = PortableModFiles.OpenRead(Payload(Slot(root, index), archive: true));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = PortableModFiles.ReadZip(archive, token);
            bytes += entries.Sum(entry => entry.Length ?? 0);
            count += entries.Count;
            if (bytes > PortableModFiles.MaxBytes || count > PortableModFiles.MaxEntries)
                throw new InvalidDataException(T("ModZIPsInThePackExceedThe"));
        }
    }

    internal static bool IsImportedContent(string path, string modRoot)
    {
        if (Path.GetFileName(path) is not ("content" or "content.zip")) return false;
        var slot = Path.GetDirectoryName(path);
        var package = slot is null ? null : Path.GetDirectoryName(slot);
        if (package is null || !SteamGameLocator.PathComparer.Equals(Path.GetDirectoryName(package), modRoot) ||
            Path.GetFileName(slot) is not { Length: 4 } slotName ||
            !int.TryParse(slotName, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        var name = Path.GetFileName(package);
        if (!name.StartsWith(LocalPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.AsSpan(LocalPrefix.Length), "N", out _)) return false;

        var marker = Path.Combine(package, ManifestFile);
        if (!File.Exists(marker)) return false;
        try
        {
            SnapshotFiles.RejectLink(modRoot);
            SnapshotFiles.RejectLink(package);
            SnapshotFiles.RejectLink(slot!);
            SnapshotFiles.RejectLink(marker);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static Manifest? ReadImportedManifest(GameEnvironment environment, Guid id)
    {
        if (!Path.IsPathFullyQualified(environment.UserDataDirectory)) return null;
        var root = Path.Combine(environment.UserDataDirectory, "mod", LocalPrefix + id.ToString("N"));
        var path = Path.Combine(root, ManifestFile);
        if (!File.Exists(path)) return null;
        Ck3LocalModInstaller.ModRoot(environment);
        SnapshotFiles.RejectLink(root);
        SnapshotFiles.RejectLink(path);
        return ParseManifest(TextDataParser.ReadSmallFile(path), id, PackageJson);
    }

    private static string PackagePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".ck3pack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(T("ChooseALocalCk3packFile"));
        return Path.GetFullPath(path);
    }
}
