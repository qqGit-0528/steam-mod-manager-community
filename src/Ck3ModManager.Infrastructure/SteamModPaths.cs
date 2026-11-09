using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal static class SteamModPaths
{
    internal static string[] FindCandidates(string home, GameDefinition game, string? installation,
        IReadOnlyList<string> libraries)
    {
        var names = new[] { game.GameName, Path.GetFileName(installation) }
            .OfType<string>().Where(IsDirectoryName).Distinct(SteamGameLocator.PathComparer);
        var roots = new List<string>();
        var engineRoots = new List<string>();
        if (installation is not null)
        {
            foreach (var directory in ChildDirectories(installation).Order(SteamGameLocator.PathComparer))
            {
                var name = Path.GetFileName(directory);
                var bundle = name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
                if (!bundle && !name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;
                var dataRoot = bundle ? Path.Combine(directory, "Contents", "Resources", "Data") : directory;
                if (bundle) engineRoots.Add(Path.Combine(directory, "Contents"));
                engineRoots.Add(dataRoot);
                if (ReadUnityIdentity(dataRoot) is not { } unity) continue;
                roots.AddRange(UnityUserRoots(home, unity.Company, unity.Product, libraries, game.AppId));
            }
        }
        var documents = Path.Combine(home, "Documents");
        if (OperatingSystem.IsWindows() && home == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var name in names)
        {
            roots.Add(Path.Combine(documents, "Paradox Interactive", name));
            roots.Add(Path.Combine(documents, "My Games", name));
            roots.Add(Path.Combine(documents, name));
            if (OperatingSystem.IsMacOS())
                roots.Add(Path.Combine(home, "Library", "Application Support", name));
            else if (OperatingSystem.IsWindows())
            {
                roots.Add(Path.Combine(home, "Saved Games", name));
                roots.Add(Path.Combine(home, "AppData", "Roaming", name));
                roots.Add(Path.Combine(home, "AppData", "Local", name));
                roots.Add(Path.Combine(home, "AppData", "LocalLow", name));
            }
            else
            {
                roots.Add(Path.Combine(XdgRoot(home, "XDG_CONFIG_HOME", ".config"), name));
                roots.Add(Path.Combine(XdgRoot(home, "XDG_DATA_HOME", Path.Combine(".local", "share")), name));
                foreach (var prefix in ProtonUserRoots(libraries, game.AppId))
                {
                    roots.Add(Path.Combine(prefix, "Documents", name));
                    roots.Add(Path.Combine(prefix, "Documents", "My Games", name));
                    roots.Add(Path.Combine(prefix, "AppData", "Roaming", name));
                    roots.Add(Path.Combine(prefix, "AppData", "Local", name));
                    roots.Add(Path.Combine(prefix, "AppData", "LocalLow", name));
                }
            }
        }
        if (installation is not null) roots.Add(installation);
        roots.AddRange(engineRoots);
        if (roots.Count == 0) roots.Add(Path.Combine(home, "Documents", game.Id));
        return roots.Distinct(SteamGameLocator.PathComparer).SelectMany(ModChildren).ToArray();
    }

    private static (string Company, string Product)? ReadUnityIdentity(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "app.info");
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new StringReader(TextDataParser.ReadSmallFile(path, 4096));
            var company = reader.ReadLine()?.Trim();
            var product = reader.ReadLine()?.Trim();
            return company is not null && product is not null && IsDirectoryName(company) && IsDirectoryName(product)
                ? (company, product) : null;
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return null; }
    }

    private static IEnumerable<string> UnityUserRoots(string home, string company, string product,
        IReadOnlyList<string> libraries, uint appId)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", company, product);
            yield return Path.Combine(home, "Library", "Application Support", $"unity.{company}.{product}");
        }
        else if (OperatingSystem.IsWindows())
            yield return Path.Combine(home, "AppData", "LocalLow", company, product);
        else
        {
            yield return Path.Combine(XdgRoot(home, "XDG_CONFIG_HOME", ".config"), "unity3d", company, product);
            foreach (var prefix in ProtonUserRoots(libraries, appId))
                yield return Path.Combine(prefix, "AppData", "LocalLow", company, product);
        }
    }

    internal static IEnumerable<string> NativeGameProtonModPaths(GameDefinition game, IReadOnlyList<string> libraries)
    {
        if (!OperatingSystem.IsLinux()) yield break;
        foreach (var prefix in ProtonUserRoots(libraries, game.AppId))
            yield return game.AppId == GameCatalog.PrisonArchitect.AppId
                ? Path.Combine(prefix, "AppData", "Local", "Introversion", "Prison Architect", game.LocalModDirectoryName)
                : Path.Combine(prefix, "Documents", "Paradox Interactive", game.GameName, game.LocalModDirectoryName);
    }

    private static IEnumerable<string> ProtonUserRoots(IReadOnlyList<string> libraries, uint appId) =>
        libraries.Select(library => Path.Combine(library, "steamapps", "compatdata", appId.ToString(CultureInfo.InvariantCulture),
            "pfx", "drive_c", "users", "steamuser"));

    private static string XdgRoot(string home, string variable, string fallback)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        return home == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && Path.IsPathFullyQualified(path ?? "")
            ? path! : Path.Combine(home, fallback);
    }

    private static string[] ChildDirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return []; }
    }

    private static IEnumerable<string> ModChildren(string root)
    {
        var existing = ChildDirectories(root);
        foreach (var name in new[] { "mod", "mods" })
        {
            var matches = existing.Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 0)
            {
                foreach (var path in matches) yield return path;
            }
            else
            {
                yield return Path.Combine(root, name);
                if (!OperatingSystem.IsWindows())
                    yield return Path.Combine(root, char.ToUpperInvariant(name[0]) + name[1..]);
            }
        }
    }

    private static bool IsDirectoryName(string name) => name.Length != 0 && name is not "." and not ".." &&
        name.IndexOfAny(['/', '\\']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
