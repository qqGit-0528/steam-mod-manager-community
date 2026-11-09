using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed record LocalModLibraryUpdate(ModScanResult Scan, bool ModsChanged);
public sealed record DuplicateModCopies(bool Confirmed, IReadOnlyList<InstalledMod> OtherCopies);

/// <summary>每个游戏工作区的本地扫描投影。调用方在工作区线程接收快照和查询。</summary>
public sealed class LocalModLibrary(IInstalledModContent? content)
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private ModScanResult? _current;
    private readonly Dictionary<string, InstalledMod> _byPath = new(Paths);
    private ILookup<ulong, InstalledMod>? _copiesByWorkshopId;
    private ILookup<string, InstalledMod>? _copiesByName;

    public LocalModLibraryUpdate? Accept(ModScanResult scan, bool replace = false, string? removedModId = null)
    {
        if (!replace && scan.ScanStartedAt != 0 && _current is { } current && scan.ScanStartedAt < current.ScanStartedAt)
            return null;

        var mods = scan.Mods.Where(mod => mod.Id != removedModId || mod.IsAvailable)
            .DistinctBy(mod => mod.Id).ToList();
        if (!replace && scan.Warnings.Count != 0 && _current is { } previous)
        {
            var present = mods.Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
            mods.AddRange(previous.Mods.Where(mod => mod.Id != removedModId && !present.Contains(mod.Id)));
        }

        var changed = replace || _current is null || !SameMods(_current.Mods, mods);
        if (changed)
        {
            _byPath.Clear();
            foreach (var mod in mods)
                if (Normalize(mod.ContentPath) is { } path) _byPath.TryAdd(path, mod);
            var available = mods.Where(mod => mod.IsAvailable && Normalize(mod.ContentPath) is not null).ToArray();
            _copiesByWorkshopId = available.Where(mod => mod.WorkshopId is > 0).ToLookup(mod => mod.WorkshopId!.Value);
            _copiesByName = available.Where(mod => !string.IsNullOrWhiteSpace(mod.Name))
                .ToLookup(mod => mod.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        _current = scan with
        {
            Mods = changed ? mods : _current!.Mods,
            Environment = scan.Environment ?? _current?.Environment,
            ScanStartedAt = Math.Max(scan.ScanStartedAt, _current?.ScanStartedAt ?? 0)
        };
        return new(_current, changed);
    }

    public DuplicateModCopies? FindDuplicateCopies(InstalledMod mod)
    {
        if (!mod.IsAvailable || Normalize(mod.ContentPath) is not { } path ||
            _copiesByWorkshopId is null || _copiesByName is null)
            return null;

        InstalledMod[] OtherPaths(IEnumerable<InstalledMod> candidates) => candidates
            .Where(other => !Paths.Equals(path, Normalize(other.ContentPath)))
            .DistinctBy(other => Normalize(other.ContentPath), Paths).ToArray();

        if (mod.WorkshopId is > 0 && OtherPaths(_copiesByWorkshopId[mod.WorkshopId.Value]) is { Length: > 0 } confirmed)
            return new(true, confirmed);

        var suspected = OtherPaths(_copiesByName[mod.Name.Trim()]
            .Where(other => mod.WorkshopId is not > 0 || other.WorkshopId is not > 0));
        return suspected.Length == 0 ? null : new(false, suspected);
    }

    public LocalInstallation FindInstallation(WorkshopDownloadSnapshot task)
    {
        if (task.State != WorkshopDownloadState.Completed || Normalize(task.InstalledPath) is not { } path || _current is null)
            return new(LocalInstallationState.Unknown);

        var match = _byPath.GetValueOrDefault(path);
        if (match is not null && (!match.IsAvailable || match.WorkshopId is { } id && id != task.PublishedFileId))
            return new(LocalInstallationState.Missing);

        var legacyContainer = match is null && task.AppId == GameCatalog.PrisonArchitect.AppId &&
            task.Owner == WorkshopDownloadOwner.SteamClient && IsLegacyWorkshopPath(path, task.PublishedFileId);
        if (match is null && !legacyContainer)
        {
            if (_current.Warnings.Count == 0 || content?.Inspect(path, requirePayload: false).State == LocalInstallationState.Missing)
                return new(LocalInstallationState.Missing);
            return new(LocalInstallationState.Unknown);
        }
        return content?.Inspect(path, requirePayload: legacyContainer) ?? new(LocalInstallationState.Unknown);
    }

    public InstalledMod? FindInstalledMod(WorkshopDownloadSnapshot task)
    {
        if (task.State != WorkshopDownloadState.Completed || Normalize(task.InstalledPath) is not { } path)
            return null;
        var mod = _byPath.GetValueOrDefault(path);
        return mod is { IsAvailable: true } && (mod.WorkshopId is null || mod.WorkshopId == task.PublishedFileId) ? mod : null;
    }

    private bool IsLegacyWorkshopPath(string path, ulong itemId)
    {
        var name = itemId.ToString(CultureInfo.InvariantCulture);
        return _current?.Environment?.WorkshopDirectories.Any(root =>
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == name + "_legacy.bin" ||
                relative.Split(Path.DirectorySeparatorChar, 2)[0] == name;
        }) == true;
    }

    private static string? Normalize(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static bool SameMods(IReadOnlyList<InstalledMod> before, IReadOnlyList<InstalledMod> after)
    {
        if (before.Count != after.Count) return false;
        for (var i = 0; i < before.Count; i++)
        {
            var current = after[i];
            if (!before[i].Dependencies.SequenceEqual(current.Dependencies) ||
                (before[i] with { Dependencies = current.Dependencies }) != current)
                return false;
        }
        return true;
    }
}
