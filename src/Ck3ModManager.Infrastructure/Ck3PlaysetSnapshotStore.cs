using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>保存独立的 CK3 Mod 文件副本，并校验后恢复为本地 Mod。</summary>
public sealed partial class Ck3PlaysetSnapshotStore(string storageRoot) : IPlaysetSnapshotStore, IPlaysetPackageStore
{
    private const string LocalPrefix = "app_mm_snapshot_";
    private const string ManifestFile = "snapshot.json";
    private const int Format = 1;
    private const int MaxMods = 1000;
    private readonly string _root = Path.Combine(Path.GetFullPath(storageRoot), "playset-snapshots");

    private sealed record Entry(string Name, bool Enabled, ulong? WorkshopId, string? SteamRevision,
        string? SupportedGameVersion, string? ModVersion, bool Archive, string? ContentHash, string? DescriptorHash);
    private sealed record Manifest(int Version, uint GameAppId, Guid Id, string Name, string GameVersion,
        DateTimeOffset CreatedAt, Entry[] Mods)
    {
        public PlaysetSnapshot Summary() => new(Id, Name, GameVersion, CreatedAt, Mods.Count(mod => mod.Enabled));
    }

    public Task<IReadOnlyList<PlaysetSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PlaysetSnapshot>>(() =>
        {
            SnapshotFiles.RejectLink(_root);
            if (!Directory.Exists(_root)) return [];
            var snapshots = new List<PlaysetSnapshot>();
            foreach (var path in Directory.EnumerateDirectories(_root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id) || id == Guid.Empty) continue;
                try { snapshots.Add(ReadManifest(id).Summary()); }
                catch (Exception error) when (SteamGameLocator.IsReadableError(error) || error is InvalidDataException) { }
            }
            return snapshots.OrderByDescending(snapshot => snapshot.CreatedAt).ToArray();
        }, cancellationToken);

    public Task<PlaysetSnapshot> CaptureAsync(GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(environment, profile, installed, cancellationToken), cancellationToken);

    public Task<ModProfile> RestoreAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default) =>
        Task.Run(() => Restore(environment, id, cancellationToken), cancellationToken);

    public Task DeleteAsync(GameEnvironment environment, Guid id, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == Guid.Empty) throw new ArgumentException(T("TheSnapshotIDIsInvalid"), nameof(id));
            var modRoot = Path.Combine(Path.GetFullPath(environment.UserDataDirectory), "mod");
            var localName = LocalPrefix + id.ToString("N");
            SnapshotFiles.RejectLink(modRoot);
            var restored = Path.Combine(modRoot, localName);
            SnapshotFiles.RejectLink(restored);
            if (Directory.Exists(restored)) Directory.Delete(restored, recursive: true);
            if (Directory.Exists(modRoot))
            {
                foreach (var path in Directory.EnumerateFiles(modRoot, localName + "_*.mod").ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SnapshotFiles.RejectLink(path);
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                var lockFile = Path.Combine(modRoot, "." + localName + ".lock");
                SnapshotFiles.RejectLink(lockFile);
                try { File.Delete(lockFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            var snapshot = SnapshotPath(id);
            SnapshotFiles.RejectLink(_root);
            SnapshotFiles.RejectLink(snapshot);
            if (Directory.Exists(snapshot)) Directory.Delete(snapshot, recursive: true);
        }, cancellationToken);

    public Task<PlaysetSnapshotComparison> CompareAsync(GameEnvironment environment, Guid id, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default) =>
        Task.Run(() => Compare(environment, id, profile, installed, cancellationToken), cancellationToken);

    private PlaysetSnapshotComparison Compare(GameEnvironment environment, Guid id, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken token)
    {
        var manifest = ReadManifest(id);
        var differences = new List<string>();
        if (environment.GameVersion is null)
            differences.Add(T("TheCurrentCK3VersionIsUnknownThe", manifest.GameVersion));
        else if (environment.GameVersion != manifest.GameVersion)
            differences.Add(T("CK3DiffersFromSnapshotVersion", environment.GameVersion, manifest.GameVersion));

        var available = installed.Where(mod => mod.IsAvailable).ToLookup(mod => mod.Id, StringComparer.Ordinal);
        var actual = new List<(string Name, ulong? WorkshopId, string? Content, string? Descriptor)>();
        foreach (var selected in profile.Mods.Where(mod => mod.Enabled))
        {
            token.ThrowIfCancellationRequested();
            var mod = available[selected.InstalledModId].FirstOrDefault();
            if (mod is null)
            {
                differences.Add(T("AnEnabledModIsMissingFiles", selected.DisplayName));
                actual.Add((selected.DisplayName, selected.WorkshopId, null, null));
                continue;
            }
            try
            {
                var revision = SteamRevision(mod);
                var descriptor = SnapshotFiles.TextHash(ReadLocalDescriptor(mod));
                var content = SnapshotFiles.Fingerprint(mod.ContentPath, null, token);
                if (revision != SteamRevision(mod)) throw new IOException(T("TheWorkshopRevisionIsChanging"));
                actual.Add((mod.Name, mod.WorkshopId, content, descriptor));
            }
            catch (Exception error) when (SteamGameLocator.IsReadableError(error) || error is InvalidDataException)
            {
                differences.Add(T("CouldNotVerifyTheFilesFor", mod.Name, error.Message));
                actual.Add((mod.Name, mod.WorkshopId, null, null));
            }
        }

        var expected = manifest.Mods.Where(mod => mod.Enabled).ToArray();
        var matches = new int[expected.Length];
        var remaining = new HashSet<int>(Enumerable.Range(0, actual.Count));
        // 先匹配确切内容，恢复后的本地副本可能没有原工坊 ID。
        for (var index = 0; index < expected.Length; index++)
        {
            var entry = expected[index];
            var match = Enumerable.Range(0, actual.Count).FirstOrDefault(i =>
                remaining.Contains(i) && actual[i].Content == entry.ContentHash && actual[i].Descriptor == entry.DescriptorHash, -1);
            matches[index] = match;
            if (match >= 0) remaining.Remove(match);
        }
        for (var index = 0; index < expected.Length; index++)
        {
            if (matches[index] >= 0) continue;
            var entry = expected[index];
            var match = Enumerable.Range(0, actual.Count).FirstOrDefault(i => remaining.Contains(i) &&
                (entry.WorkshopId is not null && actual[i].WorkshopId == entry.WorkshopId ||
                 (entry.WorkshopId is null || actual[i].WorkshopId is null) &&
                 string.Equals(actual[i].Name, entry.Name, StringComparison.OrdinalIgnoreCase)), -1);
            if (match < 0)
            {
                differences.Add(T("AnEnabledSnapshotItemIsMissingFrom", entry.Name));
                continue;
            }
            matches[index] = match;
            remaining.Remove(match);
            if (actual[match].Content is not null)
                differences.Add(T("ModFilesOrDescriptorDiffer", entry.Name));
        }
        foreach (var index in remaining.Order())
            differences.Add(T("AlsoEnabledInTheCurrentProfile", actual[index].Name));
        var order = matches.Where(index => index >= 0).ToArray();
        if (!order.SequenceEqual(order.Order()))
            differences.Add(T("TheCurrentModLoadOrderDiffersFrom"));
        return new(manifest.GameVersion, differences);
    }

    private PlaysetSnapshot Capture(GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken token)
    {
        var id = Guid.NewGuid();
        SnapshotFiles.RejectLink(_root);
        Directory.CreateDirectory(_root);
        var stage = Path.Combine(_root, ".stage-" + id.ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            var manifest = CaptureInto(stage, id, environment, profile, installed, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, SnapshotPath(id));
            return manifest.Summary();
        }
        finally { DeleteStage(stage); }
    }

    private static Manifest CaptureInto(string stage, Guid id, GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken token)
    {
        var version = GameVersion(environment);
        var name = profile.Name.Trim();
        if (name.Length == 0) throw new InvalidOperationException(T("EnterAProfileNameFirst"));
        if (profile.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.DisplayName)))
            throw new InvalidOperationException(T("TheProfileContainsAModWithAn"));
        if (profile.Mods.Count > MaxMods || profile.Mods.Select(mod => mod.InstalledModId).Distinct().Count() != profile.Mods.Count)
            throw new InvalidOperationException(T("ASnapshotSupportsUpTo1000"));
        if (!profile.Mods.Any(mod => mod.Enabled)) throw new InvalidOperationException(T("EnableTheModsYouWantToSave"));

        var entries = new Entry[profile.Mods.Count];
        var sources = new InstalledMod?[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var mod = profile.Mods[index];
            if (!mod.Enabled)
            {
                entries[index] = new Entry(mod.DisplayName, false, mod.WorkshopId, null,
                    mod.SupportedGameVersion, mod.ModVersion, false, null, null);
                continue;
            }
            var source = installed.FirstOrDefault(item => item.Id == mod.InstalledModId && item.IsAvailable)
                ?? throw new InvalidOperationException(T("ModFilesAreMissing", mod.DisplayName));
            sources[index] = source;
            var descriptor = ReadLocalDescriptor(source);
            var archive = File.Exists(source.ContentPath);
            var revision = SteamRevision(source);
            var slot = Slot(stage, index);
            Directory.CreateDirectory(slot);
            var hash = SnapshotFiles.Fingerprint(source.ContentPath, Payload(slot, archive), token);
            File.WriteAllText(Path.Combine(slot, "descriptor.mod"), descriptor);
            entries[index] = new Entry(source.Name, true, source.WorkshopId, revision, source.SupportedGameVersion,
                source.ModVersion, archive, hash, SnapshotFiles.TextHash(descriptor));
        }

        // Steam 或编辑器可能在复制时替换文件。重新核对所有源后才发布快照。
        for (var index = 0; index < sources.Length; index++)
        {
            if (sources[index] is not { } source) continue;
            var entry = entries[index];
            if (SteamRevision(source) != entry.SteamRevision ||
                SnapshotFiles.Fingerprint(source.ContentPath, null, token) != entry.ContentHash ||
                SnapshotFiles.TextHash(ReadLocalDescriptor(source)) != entry.DescriptorHash)
                throw new IOException(T("FilesChangedWhileSavingWaitForThe", source.Name));
        }

        var manifest = new Manifest(Format, GameCatalog.Ck3.AppId, id, name, version, DateTimeOffset.UtcNow, entries);
        WriteManifest(stage, manifest);
        return manifest;
    }

    private static void WriteManifest(string directory, Manifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonFiles.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new InvalidOperationException(T("TheSnapshotManifestExceeds1MiB"));
        File.WriteAllText(Path.Combine(directory, ManifestFile), json);
    }

    private ModProfile Restore(GameEnvironment environment, Guid id, CancellationToken token) =>
        Restore(environment, ReadManifest(id), SnapshotPath(id), adoptStagedPackage: false, token);

    private static ModProfile Restore(GameEnvironment environment, Manifest manifest, string sourceRoot,
        bool adoptStagedPackage, CancellationToken token)
    {
        var id = manifest.Id;
        var version = GameVersion(environment);
        CheckVersion(manifest, version);

        var localName = LocalPrefix + id.ToString("N");
        var modRoot = Path.Combine(Path.GetFullPath(environment.UserDataDirectory), "mod");
        SnapshotFiles.RejectLink(modRoot);
        Directory.CreateDirectory(modRoot);
        var lockPath = Path.Combine(modRoot, "." + localName + ".lock");
        SnapshotFiles.RejectLink(lockPath);
        using var restoreLock = adoptStagedPackage ? null :
            new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var target = Path.Combine(modRoot, localName);
        SnapshotFiles.RejectLink(target);
        var reuse = Directory.Exists(target);
        if (adoptStagedPackage && reuse) throw new IOException(T("ALocalRestoreFolderWithThisID"));
        var stage = adoptStagedPackage ? sourceRoot : Path.Combine(modRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        var published = false;
        var descriptors = new List<(string Path, string Text)>();
        var createdDescriptors = new List<string>();
        try
        {
            if (!reuse && !adoptStagedPackage) Directory.CreateDirectory(stage);
            var mods = new List<ProfileMod>();
            for (var index = 0; index < manifest.Mods.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = manifest.Mods[index];
                if (!entry.Enabled)
                {
                    mods.Add(new ProfileMod($"snapshot:{id:N}:{index}", entry.Name, "", null,
                        entry.SupportedGameVersion, entry.ModVersion) { Enabled = false });
                    continue;
                }

                var source = Slot(sourceRoot, index);
                SnapshotFiles.RejectLink(source);
                var original = Path.Combine(source, "descriptor.mod");
                SnapshotFiles.RejectLink(original);
                var text = TextDataParser.ReadSmallFile(original);
                if (SnapshotFiles.TextHash(text) != entry.DescriptorHash)
                    throw new InvalidDataException(T("SnapshotDescriptorVerificationFailed", entry.Name));
                var destination = Slot(target, index);
                var content = Payload(destination, entry.Archive);
                var staged = Slot(stage, index);
                var copy = !reuse && !adoptStagedPackage;
                if (copy) Directory.CreateDirectory(staged);
                if (SnapshotFiles.Fingerprint(Payload(source, entry.Archive), copy ? Payload(staged, entry.Archive) : null, token) != entry.ContentHash)
                    throw new InvalidDataException(T("SnapshotModFileVerificationFailed", entry.Name));
                if (reuse)
                {
                    SnapshotFiles.RejectLink(destination);
                    if (SnapshotFiles.Fingerprint(content, null, token) != entry.ContentHash)
                        throw new InvalidDataException(T("RestoredModFilesHaveChangedForMove", entry.Name, target));
                }

                var wrapper = Path.Combine(modRoot, localName + "_" + SlotName(index) + ".mod");
                var relative = Path.GetRelativePath(environment.UserDataDirectory, content).Replace('\\', '/');
                var wrapperText = LocalModDescriptor.Write(TextDataParser.Read(text), entry.Archive ? "archive" : "path", relative);
                SnapshotFiles.RejectLink(wrapper);
                if (File.Exists(wrapper) && (adoptStagedPackage || File.ReadAllText(wrapper) != wrapperText))
                    throw new InvalidDataException(T("TheLocalSnapshotDescriptorWasModified", wrapper));
                descriptors.Add((wrapper, wrapperText));
                mods.Add(new ProfileMod(Ck3ModScanner.StableLocalId(content), entry.Name, content, null,
                    entry.SupportedGameVersion, entry.ModVersion));
            }

            token.ThrowIfCancellationRequested();
            if (!reuse)
            {
                Directory.Move(stage, target);
                published = true;
            }
            foreach (var (path, text) in descriptors)
            {
                token.ThrowIfCancellationRequested();
                if (!adoptStagedPackage && File.Exists(path)) continue;
                WriteDescriptor(path, text);
                createdDescriptors.Add(path);
            }
            return new ModProfile(id, $"{manifest.Name} · 快照 {manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                version, mods, DateTimeOffset.UtcNow);
        }
        catch
        {
            foreach (var path in createdDescriptors)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (published) DeleteStage(target);
            throw;
        }
        finally { DeleteStage(stage); }
    }

    private static void WriteDescriptor(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private Manifest ReadManifest(Guid id)
    {
        SnapshotFiles.RejectLink(_root);
        var root = SnapshotPath(id);
        SnapshotFiles.RejectLink(root);
        var path = Path.Combine(root, ManifestFile);
        SnapshotFiles.RejectLink(path);
        return ParseManifest(TextDataParser.ReadSmallFile(path), id);
    }

    private static Manifest ParseManifest(string json, Guid? id, JsonSerializerOptions? options = null)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, options ?? JsonFiles.Options);
        if (manifest is null || manifest.Id == Guid.Empty || id is { } expected && manifest.Id != expected || manifest.Version != Format ||
            manifest.GameAppId != GameCatalog.Ck3.AppId || string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.GameVersion) || manifest.Mods is null || manifest.Mods.Length > MaxMods ||
            manifest.Mods.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
                (entry.Enabled && (!IsHash(entry.ContentHash) || !IsHash(entry.DescriptorHash)))) ||
            !manifest.Mods.Any(entry => entry.Enabled))
            throw new InvalidDataException(T("TheCK3SnapshotManifestIsInvalid"));
        return manifest;
    }

    public Task ValidateGameVersionAsync(GameEnvironment environment, Guid profileId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(SnapshotPath(profileId))) CheckVersion(ReadManifest(profileId), GameVersion(environment));
            else if (ReadImportedManifest(environment, profileId) is { } imported) CheckVersion(imported, GameVersion(environment));
        }, cancellationToken);

    private static void CheckVersion(Manifest manifest, string version)
    {
        if (!string.Equals(manifest.GameVersion, version, StringComparison.Ordinal))
            throw new InvalidOperationException(T("TheSnapshotUsesCK3AndTheCurrent", manifest.GameVersion, version));
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ReadLocalDescriptor(InstalledMod mod)
    {
        SnapshotFiles.RejectLink(mod.DescriptorPath);
        var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(mod.DescriptorPath));
        if (string.IsNullOrWhiteSpace(properties.Value("name")))
            properties = [.. properties.Where(property => !property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)),
                new("name", mod.Name, null)];
        return LocalModDescriptor.Write(properties);
    }

    private static string? SteamRevision(InstalledMod mod) => mod.WorkshopId is { } itemId
        ? SteamClientWorkshopDownloader.TryReadRevision(mod.ContentPath, GameCatalog.Ck3.AppId, itemId) : null;

    private static string GameVersion(GameEnvironment environment) =>
        environment.GameDirectory is not null && environment.GameVersion is { Length: > 0 } version ? version :
            throw new InvalidOperationException(T("ConfirmYourLocalCK3InstallationAndGame"));

    private string SnapshotPath(Guid id) => Path.Combine(_root, id.ToString("N"));
    private static string Slot(string root, int index) => Path.Combine(root, SlotName(index));
    private static string SlotName(int index) => index.ToString("D4", CultureInfo.InvariantCulture);
    private static string Payload(string slot, bool archive) => Path.Combine(slot, archive ? "content.zip" : "content");

    internal static bool IsRestoredDescriptor(string descriptor, string content, string userData)
    {
        var name = Path.GetFileNameWithoutExtension(descriptor);
        if (!name.StartsWith(LocalPrefix, StringComparison.Ordinal) || name.Length != LocalPrefix.Length + 37) return false;
        var suffix = name.AsSpan(LocalPrefix.Length);
        if (!Guid.TryParseExact(suffix[..32], "N", out _) || suffix[32] != '_' ||
            !int.TryParse(suffix[33..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
        var modRoot = Path.Combine(userData, "mod");
        var expected = Slot(Path.Combine(modRoot, LocalPrefix + suffix[..32].ToString()), index);
        return SteamGameLocator.PathComparer.Equals(Path.GetDirectoryName(descriptor), modRoot) &&
            SteamGameLocator.PathComparer.Equals(Path.GetDirectoryName(content), expected);
    }

    private static void DeleteStage(string stage)
    {
        try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
