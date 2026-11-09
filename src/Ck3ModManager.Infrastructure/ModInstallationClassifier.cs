using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal sealed class ModInstallationClassifier(string? gameDirectory, IReadOnlyList<string> localRoots,
    IReadOnlyList<string> workshopRoots, FileWorkshopDownloadStore? store, uint appId)
{
    private readonly string? _gameDirectory = Normalize(gameDirectory);
    private readonly string[] _workshopRoots = NormalizeRoots(workshopRoots);
    public IReadOnlyList<string> LocalRoots { get; } = NormalizeRoots(localRoots);

    public InstalledMod Classify(InstalledMod mod) =>
        mod with { Management = Locate(mod.ContentPath, mod.WorkshopId).Management };

    public (ModInstallManagement Management, string? Root) Locate(string path, ulong? workshopId)
    {
        if (!Path.IsPathFullyQualified(path) || Normalize(path) is not { } full ||
            _gameDirectory is not null && IsWithin(full, _gameDirectory))
            return (ModInstallManagement.External, null);

        var parent = Path.GetDirectoryName(full);
        var workshopRoot = _workshopRoots.FirstOrDefault(root => SteamGameLocator.PathComparer.Equals(parent, root));
        if (workshopRoot is not null)
        {
            if (workshopId is not > 0)
                return (ModInstallManagement.External, null);

            var expected = workshopId.Value.ToString(CultureInfo.InvariantCulture);
            var item = Path.GetFileName(full);
            if (item == expected || Path.GetFileNameWithoutExtension(item) == expected && File.Exists(full))
                return (ModInstallManagement.Steam, workshopRoot);
            return (ModInstallManagement.External, null);
        }

        if (_workshopRoots.Any(root => IsWithin(root, full))) return (ModInstallManagement.External, null);
        var localRoot = LocalRoots.FirstOrDefault(root => SteamGameLocator.PathComparer.Equals(root, parent) ||
            appId == Ck3.AppId && Ck3PlaysetSnapshotStore.IsImportedContent(full, root));
        if (localRoot is not null) return (ModInstallManagement.Local, localRoot);
        if (workshopId is > 0 && store?.IsManagedRevision(appId, workshopId.Value, full) == true)
            return (ModInstallManagement.Application, null);
        return (ModInstallManagement.External, null);
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots) =>
        roots.Select(Normalize).OfType<string>().Distinct(SteamGameLocator.PathComparer).ToArray();

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return null; }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
