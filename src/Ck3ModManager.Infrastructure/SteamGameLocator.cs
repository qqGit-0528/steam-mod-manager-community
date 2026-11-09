using static Ck3ModManager.Core.AppText;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>只检查已知 Steam 根目录及其声明的库，不遍历用户磁盘。</summary>
public sealed class SteamGameLocator : IGameLocator
{
    private readonly IEnumerable<string>? steamRoots;
    private readonly string home;
    private readonly GameDefinition definition;
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public SteamGameLocator() : this(GameCatalog.Ck3) { }
    public SteamGameLocator(IEnumerable<string>? steamRoots, string? homeDirectory = null) : this(GameCatalog.Ck3, steamRoots, homeDirectory) { }
    public SteamGameLocator(GameDefinition game, IEnumerable<string>? steamRoots = null, string? homeDirectory = null)
    {
        definition = game ?? throw new ArgumentNullException(nameof(game));
        this.steamRoots = steamRoots;
        home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(options, cancellationToken), cancellationToken);

    private GameEnvironment Discover(DiscoveryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var (libraries, knownLibraries, knownRoots) = FindLibraries(options.SteamDirectory, warnings, cancellationToken);

        string? game = null;
        if (!string.IsNullOrWhiteSpace(options.GameDirectory))
        {
            try
            {
                var manual = Path.GetFullPath(options.GameDirectory);
                if (definition.IsDiscovered ? Directory.Exists(manual) : LooksLikeGame(manual)) game = manual;
                else warnings.Add(definition.IsDiscovered ? T("TheSpecifiedGameFolderDoesNotExist")
                    : T("TheGameFolderIsMissingOrDoes", definition.ExecutableName, definition.ShortName));
            }
            catch (Exception ex) when (IsReadableError(ex)) { warnings.Add(T("CouldNotReadTheSpecifiedGameFolder", ex.Message)); }
        }
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game is not null) break;
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{definition.AppId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var installation = ReadInstallation(library, manifest, definition.AppId);
                if (installation.HasContent && (installation.FullyInstalled || LooksLikeInstalledGame(installation.Directory)))
                    game = installation.Directory;
                else warnings.Add(T("FilesForInTheSteamManifestAre", definition.ShortName, installation.Directory));
            }
            catch (Exception ex) when (IsReadableError(ex)) { warnings.Add(T("CouldNotReadTheInstallManifest", definition.ShortName, manifest, ex.Message)); }
        }
        if (game is null) warnings.Add(T("NoInstallationOfWasFoundSetThe", definition.ShortName));
        var workshop = libraries.Select(library => WorkshopPath(library, definition.AppId)).Where(Directory.Exists).ToArray();
        string? userOverride = null;
        if (!string.IsNullOrWhiteSpace(options.UserDataDirectory))
        {
            try { userOverride = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.UserDataDirectory)); }
            catch (Exception ex) when (IsReadableError(ex))
            { warnings.Add(T("TheSelectedUserDataFolderIsInvalid", ex.Message)); }
        }

        var localCandidates = definition.IsDiscovered && userOverride is null
            ? SteamModPaths.FindCandidates(home, definition, game, libraries)
            : [definition.IsDiscovered && userOverride is not null
                ? userOverride
                : Path.Combine(userOverride ?? DefaultUserData(), definition.LocalModDirectoryName)];
        if (!definition.IsDiscovered && userOverride is null)
            localCandidates = localCandidates.Concat(SteamModPaths.NativeGameProtonModPaths(definition, libraries))
                .Distinct(PathComparer).ToArray();
        var localDirectories = definition.IsDiscovered ? localCandidates.Where(Directory.Exists).ToArray() : localCandidates;
        var userData = userOverride ?? DefaultUserData();
        if (!definition.IsDiscovered && userOverride is null && !Directory.Exists(userData))
            userData = localCandidates.Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists) ?? userData;
        if (definition.IsDiscovered)
        {
            var local = userOverride ?? localDirectories.FirstOrDefault();
            userData = local is not null
                ? Path.GetDirectoryName(local) ?? local
                : localCandidates.Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists) ?? game ?? userData;
        }
        var version = game is null ? null : ReadVersion(game, warnings);
        if (game is not null && version is null) warnings.Add(T("TheGameVersionCouldNotBeRead"));
        return new GameEnvironment(game, userData, version, workshop, warnings)
        {
            LocalModDirectories = localDirectories,
            LocalModWatchPaths = localCandidates
                .Concat(knownLibraries.Select(library => WorkshopPath(library, definition.AppId)))
                .Concat(knownRoots.Select(root => Path.Combine(root, "steamapps", "libraryfolders.vdf")))
                .Distinct(PathComparer).ToArray()
        };
    }

    internal (IReadOnlyList<string> Libraries, IReadOnlyList<string> KnownLibraries, IReadOnlyList<string> KnownRoots)
        FindLibraries(string? steamDirectory, List<string> warnings, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        var knownRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(steamDirectory)) AddKnown(steamDirectory, knownRoots);
        if (!string.IsNullOrWhiteSpace(steamDirectory)) AddExisting(steamDirectory, roots, warnings, T("SelectedSteamFolder"));
        foreach (var root in (steamRoots ?? DefaultRoots()).Concat([definition.SteamLibraryHint]).OfType<string>().Concat(definition.SteamLibraryRoots))
        {
            AddKnown(root, knownRoots);
            AddExisting(root, roots, null, T("SteamFolder"));
        }
        roots = roots.Distinct(PathComparer).ToList();
        if (roots.Count == 0) warnings.Add(T("SteamInstallationNotFoundSetItsFolder"));

        var libraries = new List<string>(roots);
        var knownLibraries = new List<string>(knownRoots);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                var entries = TextDataParser.Read(TextDataParser.ReadSmallFile(vdf, 5_242_880));
                var block = entries.FirstOrDefault(p => p.Name.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase))?.Block;
                if (block is null) throw new FormatException(T("MissingLibraryfoldersNode"));
                foreach (var entry in TextDataParser.ReadTokens(block))
                {
                    if (!int.TryParse(entry.Name, out _)) continue;
                    var path = entry.Scalar ?? (entry.Block is null ? null : TextDataParser.ReadTokens(entry.Block).Value("path"));
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        AddKnown(path, knownLibraries);
                        AddExisting(path, libraries, warnings, T("SteamLibraryFolder"));
                    }
                }
            }
            catch (Exception ex) when (IsReadableError(ex)) { warnings.Add(T("CouldNotReadSteamLibraryManifest", vdf, ex.Message)); }
        }
        return (libraries.Distinct(PathComparer).ToArray(), knownLibraries, knownRoots);
    }

    internal static string WorkshopPath(string library, uint appId) =>
        Path.Combine(library, "steamapps", "workshop", "content", appId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static (string Directory, string Name, bool FullyInstalled, bool HasContent) ReadInstallation(
        string library, string manifest, uint appId)
    {
        var root = TextDataParser.Read(TextDataParser.ReadSmallFile(manifest));
        var state = root.FirstOrDefault(p => p.Name.Equals("AppState", StringComparison.OrdinalIgnoreCase))?.Block;
        if (state is null) throw new FormatException(T("MissingAppStateNode"));
        var fields = TextDataParser.ReadTokens(state);
        if (fields.Value("appid") != appId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new FormatException(T("TheManifestAppIDDoesNotMatch"));
        var installDirectory = fields.Value("installdir") ?? throw new FormatException(T("MissingInstalldir"));
        var common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
        var candidate = Path.GetFullPath(Path.Combine(common, installDirectory));
        if (!IsWithin(common, candidate)) throw new FormatException(T("TheManifestInstallPathIsOutsideSteam"));
        var stateFlags = fields.Value("StateFlags");
        const uint fullyInstalled = 4;
        var name = fields.Value("name")?.Trim();
        return (candidate, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(candidate) : name,
            stateFlags is null || (uint.TryParse(stateFlags, out var flags) && (flags & fullyInstalled) != 0),
            Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any());
    }

    private IEnumerable<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam");
        }
        else if (OperatingSystem.IsMacOS()) yield return Path.Combine(home, "Library", "Application Support", "Steam");
        else
        {
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".steam", "root");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
    }

    private string DefaultUserData()
    {
        if (!string.IsNullOrWhiteSpace(definition.UserDataDirectory))
        {
            var custom = definition.UserDataDirectory!;
            return custom.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, custom[2..]) : custom;
        }
        if (OperatingSystem.IsLinux())
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(home, ".local", "share") : data,
                "Paradox Interactive", definition.GameName);
        }
        var documents = Path.Combine(home, "Documents");
        if (OperatingSystem.IsWindows() && home == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Paradox Interactive", definition.GameName);
    }

    private static string? ReadVersion(string game, List<string> warnings)
    {
        foreach (var path in new[] { Path.Combine(game, "launcher-settings.json"), Path.Combine(game, "launcher", "launcher-settings.json"), Path.Combine(game, "Launcher", "launcher-settings.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(TextDataParser.ReadSmallFile(path));
                foreach (var key in new[] { "version", "gameVersion" })
                    if (document.RootElement.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String && ValidVersion(field.GetString()) is { } version) return version;
            }
            catch (Exception ex) when (IsReadableError(ex)) { warnings.Add(T("CouldNotReadGameVersionFile", path, ex.Message)); }
        }
        var textPath = Path.Combine(game, "version.txt");
        if (File.Exists(textPath))
        {
            try { return ValidVersion(TextDataParser.ReadSmallFile(textPath, 4096).Trim()); }
            catch (Exception ex) when (IsReadableError(ex)) { warnings.Add(T("CouldNotReadGameVersionFile", textPath, ex.Message)); }
        }
        return null;
    }
    private static string? ValidVersion(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, @"^(\d+\.\d+(?:\.\d+){0,3})(?:\s+\([^\r\n]*\))?$");
        if (match.Success) return match.Groups[1].Value;
        // 部分游戏用「代号_数字」的版本串，例如 Prison Architect 的 The_Jailhouse_1.02。
        var trailing = Regex.Match(trimmed, @"(\d+\.\d+(?:\.\d+){0,3})$");
        return trailing.Success ? trailing.Groups[1].Value : null;
    }
    private bool LooksLikeGame(string path) => Directory.Exists(path) && definition.ExecutablePaths.Any(relative =>
        File.Exists(Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))));

    // 清单已确认安装时才允许放宽：可执行文件命名各游戏差异很大（PrisonArchitect.app、binaries/victoria3.app 等）。
    // 手动指定的目录仍走严格校验，避免把其他游戏的目录当成当前游戏。
    private bool LooksLikeInstalledGame(string path) =>
        LooksLikeGame(path)
        || File.Exists(Path.Combine(path, "launcher-settings.json"))
        || File.Exists(Path.Combine(path, "launcher", "launcher-settings.json"))
        || File.Exists(Path.Combine(path, "Launcher", "launcher-settings.json"))
        || Directory.Exists(Path.Combine(path, "binaries"));
    private static void AddExisting(string path, List<string> paths, List<string>? warnings, string label)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full)) paths.Add(full);
            else warnings?.Add(T("DoesNotExist", label, full));
        }
        catch (Exception ex) when (IsReadableError(ex)) { warnings?.Add(T("IsInvalid", label, ex.Message)); }
    }
    private static void AddKnown(string path, List<string> paths)
    {
        try { paths.Add(Path.GetFullPath(path)); }
        catch (Exception ex) when (IsReadableError(ex)) { }
    }
    internal static bool IsWithin(string root, string candidate) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static bool IsReadableError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or JsonException;
}
