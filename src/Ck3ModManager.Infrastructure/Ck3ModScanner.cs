using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class Ck3ModScanner(bool includeGenericContent = false, FileWorkshopDownloadStore? store = null, uint appId = Ck3.AppId) : IModScanner
{
    private static readonly HashSet<string> GenericModExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".pak", ".package", ".jar", ".scs", ".dll", ".vpk", ".bin", ".tmod"
    };

    public Task<ModScanResult> ScanAsync(GameEnvironment environment, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(environment.UserDataDirectory, environment.LocalModDirectories, environment.WorkshopDirectories,
            cancellationToken, environment.GameDirectory), cancellationToken);

    public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(userDataDirectory, null, workshopDirectories, cancellationToken), cancellationToken);

    private ModScanResult Scan(string userDataDirectory, IReadOnlyList<string>? localModDirectories,
        IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken, string? gameDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var mods = new Dictionary<string, InstalledMod>(StringComparer.Ordinal);
        string userData;
        try { userData = Path.GetFullPath(userDataDirectory); }
        catch (Exception ex) when (SteamGameLocator.IsReadableError(ex))
        { return new ModScanResult([], [T("InvalidUserDataFolder", ex.Message)]); }
        var workshop = new List<string>();
        foreach (var directory in workshopDirectories)
        {
            try { workshop.Add(Path.GetFullPath(directory)); }
            catch (Exception ex) when (SteamGameLocator.IsReadableError(ex)) { warnings.Add(T("InvalidWorkshopFolder", ex.Message)); }
        }
        foreach (var modDirectory in localModDirectories ?? [Path.Combine(userData, "mod")])
        {
            var dataRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(modDirectory)) ?? userData;
            foreach (var descriptor in ListFiles(modDirectory, "*.mod", warnings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadMod(descriptor, null, null, dataRoot, workshop, mods, warnings);
            }
            foreach (var content in ListDirectories(modDirectory, warnings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 本地导入的受控目录在外层 .mod 发布后才进入 CK3 扫描结果。
                if (appId == Ck3.AppId && Ck3LocalModInstaller.IsManagedDirectory(Path.GetFileName(content)))
                    continue;
                if (File.Exists(Path.Combine(content, ".metadata", "metadata.json")))
                    ReadMetadata(content, null, mods, warnings);
                else if (includeGenericContent && File.Exists(Path.Combine(content, "descriptor.mod")))
                    ReadMod(Path.Combine(content, "descriptor.mod"), content, null, dataRoot, workshop, mods, warnings);
                if (includeGenericContent && !Path.GetFileName(content).StartsWith('.')) AddGenericMod(content, null, mods);
            }
            if (includeGenericContent)
                foreach (var file in ListFiles(modDirectory, "*", warnings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (GenericModExtensions.Contains(Path.GetExtension(file))) AddGenericMod(file, null, mods);
                }
        }
        foreach (var directory in workshop.Distinct(SteamGameLocator.PathComparer))
        {
            foreach (var content in ListDirectories(directory, warnings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = ParseId(Path.GetFileName(content));
                if (id is null) continue;
                ReadWorkshopContent(content, id.Value, userData, workshop, mods, warnings, includeGenericContent);
            }
            if (includeGenericContent)
                foreach (var file in ListFiles(directory, "*", warnings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ParseId(Path.GetFileNameWithoutExtension(file)) is { } id) AddGenericMod(file, id, mods);
                }
        }
        var locations = new ModInstallationClassifier(gameDirectory, localModDirectories ?? [Path.Combine(userData, "mod")],
            workshop, store, appId);
        return new ModScanResult(mods.Values.Select(locations.Classify).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray(), warnings);
    }

    internal static bool IsRecognizableWorkshopContent(string content, ulong itemId)
    {
        if (!Directory.Exists(content)) return false;
        try
        {
            var descriptor = Path.Combine(content, "descriptor.mod");
            if (!File.Exists(descriptor)) descriptor = Directory.EnumerateFiles(content, "*.mod").FirstOrDefault();
            if (descriptor is not null)
            {
                if (string.IsNullOrWhiteSpace(TextDataParser.Read(TextDataParser.ReadSmallFile(descriptor)).Value("name"))) return false;
            }
            else
            {
                using var metadata = JsonDocument.Parse(TextDataParser.ReadSmallFile(Path.Combine(content, ".metadata", "metadata.json")));
                if (metadata.RootElement.ValueKind != JsonValueKind.Object || !metadata.RootElement.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) return false;
            }
        }
        catch (Exception error) when (error is JsonException || SteamGameLocator.IsReadableError(error)) { return false; }
        var mods = new Dictionary<string, InstalledMod>(StringComparer.Ordinal);
        ReadWorkshopContent(content, itemId, content, [], mods, []);
        return mods.Values.Any(mod => mod.IsAvailable && mod.WorkshopId == itemId);
    }

    private static void ReadWorkshopContent(string content, ulong id, string userData, IReadOnlyList<string> workshop,
        Dictionary<string, InstalledMod> mods, List<string> warnings, bool includeGenericContent = false)
    {
        var standard = Path.Combine(content, "descriptor.mod");
        var descriptor = File.Exists(standard) ? standard : ListFiles(content, "*.mod", warnings).FirstOrDefault();
        if (descriptor is not null)
            ReadMod(descriptor, content, id, userData, workshop, mods, warnings);
        else if (File.Exists(Path.Combine(content, ".metadata", "metadata.json")))
            ReadMetadata(content, id, mods, warnings);
        else if (!includeGenericContent)
            warnings.Add(T("WorkshopModHasNoDescriptorModMod", id, content));
        if (includeGenericContent) AddGenericMod(content, id, mods);
    }

    private static void AddGenericMod(string content, ulong? workshopId, Dictionary<string, InstalledMod> mods)
    {
        var id = StableLocalId(content);
        if (mods.ContainsKey(id)) return;
        var name = workshopId is { } itemId ? $"工坊项目 {itemId}" : Path.GetFileName(content);
        mods.Add(id, new InstalledMod(id, name, content, "", null, null, workshopId, [],
            workshopId is null ? ModOrigin.Local : ModOrigin.SteamWorkshop, true));
    }

    private static void ReadMetadata(string content, ulong? workshopId, Dictionary<string, InstalledMod> mods, List<string> warnings)
    {
        var descriptor = Path.Combine(content, ".metadata", "metadata.json");
        try
        {
            using var document = JsonDocument.Parse(TextDataParser.ReadSmallFile(descriptor));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(T("ModMetadataJsonMustBeAJSON"));
            string? Field(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = StableLocalId(content);
            mods.TryAdd(id, new InstalledMod(id, Field("name") ?? Path.GetFileName(content), content, descriptor,
                Field("version"), Field("supported_game_version"), workshopId, [],
                workshopId is null ? ModOrigin.Local : ModOrigin.SteamWorkshop, Directory.Exists(content)));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException || SteamGameLocator.IsReadableError(error))
        { warnings.Add(T("CouldNotReadModMetadataJsonAt", descriptor, error.Message)); }
    }

    private static void ReadMod(string descriptor, string? knownContent, ulong? knownId, string userData,
        IReadOnlyList<string> workshop, Dictionary<string, InstalledMod> mods, List<string> warnings)
    {
        try
        {
            var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(descriptor));
            var content = knownContent;
            if (content is null)
            {
                var path = properties.Value("path") ?? properties.Value("archive");
                if (string.IsNullOrWhiteSpace(path))
                {
                    content = Path.Combine(Path.GetDirectoryName(descriptor)!, Path.GetFileNameWithoutExtension(descriptor));
                    warnings.Add(T("TheDescriptorHasNoPathOrArchive", descriptor));
                }
                else content = ResolvePath(path, descriptor, userData);
            }
            var wrapper = properties;
            var isSnapshot = Ck3PlaysetSnapshotStore.IsRestoredDescriptor(descriptor, content, userData);
            var inner = Path.Combine(content, "descriptor.mod");
            if (!isSnapshot && Directory.Exists(content) && File.Exists(inner) && !SteamGameLocator.PathComparer.Equals(Path.GetFullPath(inner), Path.GetFullPath(descriptor)))
            {
                try { properties = TextDataParser.Read(TextDataParser.ReadSmallFile(inner)); }
                catch (Exception ex) when (SteamGameLocator.IsReadableError(ex)) { warnings.Add(T("CouldNotReadTheLocalDescriptorMod", inner, ex.Message)); }
            }
            string? Field(string key) => properties.Value(key) ?? wrapper.Value(key);
            // 内容可能由作者复制旧项目而保留旧 ID；工坊目录或安装 wrapper 的来源身份优先。
            var workshopId = isSnapshot ? null : knownId ?? ParseId(wrapper.Value("remote_file_id"))
                ?? ParseId(properties.Value("remote_file_id")) ?? InferWorkshopId(content, workshop);
            var isArchive = knownContent is null && wrapper.Value("path") is null && wrapper.Value("archive") is not null;
            var isAvailable = Directory.Exists(content) || (isArchive && File.Exists(content));
            if (!isAvailable) warnings.Add(T("ModContentIsMissingOrHasNot", Field("name") ?? Path.GetFileNameWithoutExtension(descriptor), content));
            var name = Field("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(descriptor);
                warnings.Add(T("TheDescriptorHasNoNameUsingThe", descriptor));
            }
            var dependencyBlock = properties.LastOrDefault(p => p.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))?.Block
                ?? wrapper.LastOrDefault(p => p.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))?.Block;
            var dependencies = dependencyBlock?.Where(x => x is not "{" and not "}" and not "=").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            // Two installed copies of the same Workshop project may contain different releases.
            // Deduplicate wrappers by content location, never by Workshop project ID alone.
            var id = StableLocalId(content);
            var mod = new InstalledMod(id, name, content, descriptor, Field("version"), Field("supported_version"), workshopId,
                dependencies, workshopId is null ? ModOrigin.Local : ModOrigin.SteamWorkshop, isAvailable);
            if (!mods.TryGetValue(id, out var existing) || (!existing.IsAvailable && mod.IsAvailable)) mods[id] = mod;
        }
        catch (Exception ex) when (SteamGameLocator.IsReadableError(ex)) { warnings.Add(T("SkippedUnreadableModDescriptor", descriptor, ex.Message)); }
    }

    internal static string ResolvePath(string value, string descriptor, string userData)
    {
        // Paradox descriptors use either escaped Windows paths or slash-separated relative paths.
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized)) return Path.GetFullPath(normalized);
        if (!OperatingSystem.IsWindows() && value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':')
            throw new InvalidDataException(T("TheDescriptorPointsToAnAbsoluteWindows"));
        var userCandidate = Path.GetFullPath(Path.Combine(userData, normalized));
        var descriptorCandidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(descriptor)!, normalized));
        if (Directory.Exists(userCandidate) || File.Exists(userCandidate)) return userCandidate;
        if (Directory.Exists(descriptorCandidate) || File.Exists(descriptorCandidate)) return descriptorCandidate;
        return normalized.StartsWith("mod" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? userCandidate : descriptorCandidate;
    }

    private static ulong? InferWorkshopId(string content, IReadOnlyList<string> workshop)
    {
        foreach (var root in workshop)
            if (SteamGameLocator.PathComparer.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(content)), Path.TrimEndingDirectorySeparator(root)))
                return ParseId(Path.GetFileName(Path.TrimEndingDirectorySeparator(content)));
        return null;
    }
    private static ulong? ParseId(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    internal static string StableLocalId(string content)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(content));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "installed:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    }
    private static IEnumerable<string> ListFiles(string directory, string pattern, List<string> warnings)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (SteamGameLocator.IsReadableError(ex)) { warnings.Add(T("CouldNotScanFolder", directory, ex.Message)); return []; }
    }
    private static IEnumerable<string> ListDirectories(string directory, List<string> warnings)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (SteamGameLocator.IsReadableError(ex)) { warnings.Add(T("CouldNotScanFolder", directory, ex.Message)); return []; }
    }
}
