using static Ck3ModManager.Core.AppText;
using System.Diagnostics;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed record WorkspaceState(
    AppSettings Settings,
    GameEnvironment Environment,
    ModScanResult Scan,
    IReadOnlyList<ModProfile> Profiles);

public sealed record GameProfilePreparation(GameProfileApplyResult? Applied, Exception? SaveError = null);

public sealed partial class WorkspaceService(
    IGameLocator gameLocator,
    IModScanner modScanner,
    IProfileStore profileStore,
    ISettingsStore settingsStore,
    IGameModActivator? gameModActivator = null,
    IPlaysetSnapshotStore? playsetSnapshots = null,
    ICk3SaveBindingStore? saveBindings = null,
    IWorkshopStatusSource? workshopStatus = null,
    IInstalledModRemover? installedModRemover = null,
    IInstalledModContent? installedModContent = null,
    IFileConflictScanner? fileConflicts = null,
    ILocalModInstaller? localModInstaller = null,
    ICk3DiagnosticsReader? diagnostics = null)
{
    public LocalModLibrary LocalMods { get; } = new(installedModContent);

    public bool HasGameModActivation => gameModActivator is not null;

    public bool HasCk3Diagnostics => diagnostics is not null;

    public async Task<Ck3DiagnosticsReport> ReadCk3DiagnosticsAsync(AppSettings settings, CancellationToken token = default)
    {
        var reader = diagnostics ?? throw new NotSupportedException(T("CK3LogDiagnosticsAreUnavailableForThis"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), token);
        var report = await reader.ReadAsync(environment, token);
        token.ThrowIfCancellationRequested();
        return report;
    }


    public bool HasInstalledModRemoval => installedModRemover is not null;

    public async Task<ModScanResult> RemoveInstalledModAsync(AppSettings settings, InstalledMod selected,
        CancellationToken cancellationToken = default)
    {
        var remover = installedModRemover ?? throw new NotSupportedException(T("ModRemovalIsUnavailableInThisEnvironment"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        var current = scan.Mods.FirstOrDefault(mod => mod.Id == selected.Id && mod.IsAvailable &&
            mod.ContentPath == selected.ContentPath && mod.DescriptorPath == selected.DescriptorPath &&
            mod.Name == selected.Name && mod.WorkshopId == selected.WorkshopId && mod.Management == selected.Management);
        if (current is null) throw new InvalidOperationException(T("ThisModInstallationChangedRescanAndSelect"));

        var relativePrefix = ".." + Path.DirectorySeparatorChar;
        if (scan.Mods.Any(other => other.Id != current.Id && other.IsAvailable &&
            Path.GetRelativePath(current.ContentPath, other.ContentPath) is var relative &&
            relative != ".." && !relative.StartsWith(relativePrefix, StringComparison.Ordinal) && !Path.IsPathRooted(relative)))
            throw new InvalidOperationException(T("ThisFolderContainsAnotherInstalledModCheck"));

        await remover.RemoveAsync(environment, current, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var refreshed = await modScanner.ScanAsync(environment, CancellationToken.None);
        if (refreshed.Mods.Any(mod => mod.Id == current.Id && mod.IsAvailable))
            throw new IOException(T("TheModIsStillPresentAfterRescanning"));
        return refreshed with { Environment = environment, ScanStartedAt = started };
    }

    public bool HasPlaysetSnapshots => playsetSnapshots is not null;

    public bool HasFileConflictCheck => fileConflicts is not null;

    public async Task<FileConflictReport> CheckFileConflictsAsync(AppSettings settings, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        var scanner = fileConflicts ?? throw new NotSupportedException(T("FileConflictChecksAreUnavailableForThis"));
        var (_, scan) = await DiscoverAndScanAsync(settings, cancellationToken);
        var available = scan.Mods.Where(mod => mod.IsAvailable).ToLookup(mod => mod.Id, StringComparer.Ordinal);
        var candidates = new List<FileConflictMod>();
        var warnings = new List<string>();

        for (var index = 0; index < profile.Mods.Count; index++)
        {
            var entry = profile.Mods[index];
            if (!entry.Enabled) continue;
            var installed = available[entry.InstalledModId].FirstOrDefault();
            if (installed is null)
                warnings.Add(T("CurrentInstallationNotFound", index + 1, entry.DisplayName));
            else
                candidates.Add(new(index + 1, installed.Name, installed.ContentPath) { DescriptorPath = installed.DescriptorPath });
        }

        var result = await scanner.CheckAsync(candidates, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Warnings = warnings.Concat(result.Warnings).ToArray() };
    }

    public bool HasWorkshopStatus => workshopStatus is not null;

    public async Task<WorkshopUpdateReport> CheckWorkshopUpdatesAsync(AppSettings settings, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        var source = workshopStatus ?? throw new NotSupportedException(T("BatchWorkshopStatusChecksAreUnavailableFor"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        var enabled = profile.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId).ToHashSet(StringComparer.Ordinal);
        var candidates = scan.Mods.Where(mod => enabled.Contains(mod.Id) &&
            mod.IsAvailable && mod.Management == ModInstallManagement.Steam && mod.WorkshopId is > 0)
            .DistinctBy(mod => mod.WorkshopId).ToArray();
        if (candidates.Length == 0) return new([], T("ThisProfileHasNoEnabledInstalledSteam"));

        var status = await source.CheckAsync(environment, candidates.Select(mod => mod.WorkshopId!.Value).ToArray(), cancellationToken);
        return new(candidates.Select(mod =>
            new WorkshopUpdateEntry(mod.WorkshopId!.Value, mod.Name, status.States.GetValueOrDefault(mod.WorkshopId.Value))).ToArray(),
            status.Note);
    }

    public bool HasSaveBindings => saveBindings is not null && playsetSnapshots is not null;

    public Task<Ck3SaveSelection> ReadSaveAsync(string path, CancellationToken cancellationToken = default) =>
        (saveBindings ?? throw new NotSupportedException(T("SaveBindingsAreUnavailableForThisGame"))).ReadAsync(path, cancellationToken);

    public async Task<Ck3SaveSelection> BindSaveAsync(Ck3SaveSelection save, Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await ListPlaysetSnapshotsAsync(cancellationToken);
        if (!snapshots.Any(snapshot => snapshot.Id == snapshotId))
            throw new InvalidOperationException(T("TheSelectedSnapshotIsMissingChooseAnother"));
        return await (saveBindings ?? throw new NotSupportedException(T("SaveBindingsAreUnavailableForThisGame")))
            .BindAsync(save, snapshotId, cancellationToken);
    }

    public async Task<Ck3SaveCheck> CheckSaveAsync(AppSettings settings, string path, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        var save = await ReadSaveAsync(path, cancellationToken);
        if (save.Stamp is null)
            return new(save, [T("TheSaveMovedOrIsUnavailableSelect")], false);
        if (save.NeedsRebind)
            return new(save, [T("TheSaveChangedConfirmItsBindingAgain")], false);
        if (save.Binding is not { } binding)
            return new(save, [T("BindASnapshotToThisSaveFirst")], false);
        var snapshots = playsetSnapshots ?? throw new NotSupportedException(T("PlaysetSnapshotsAreUnavailableForThisGame"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        PlaysetSnapshotComparison comparison;
        try
        {
            comparison = await snapshots.CompareAsync(environment, binding.SnapshotId, profile, scan.Mods, cancellationToken);
        }
        catch (FileNotFoundException) { return new(save, [T("TheBoundSnapshotIsMissingBindAnother")], false); }
        catch (DirectoryNotFoundException) { return new(save, [T("TheBoundSnapshotIsMissingBindAnother")], false); }
        var current = await ReadSaveAsync(path, cancellationToken);
        if (current.Stamp != save.Stamp || current.Binding != binding)
            return new(current, [T("TheSaveOrBindingChangedDuringThe")], false);
        var differences = comparison.Differences.ToList();
        if (save.Metadata.GameVersion is { } version && version != comparison.GameVersion)
            differences.Insert(0, T("TheSaveRecordsCK3ItsBoundSnapshot", version, comparison.GameVersion));
        return new(save, differences, environment.GameVersion == comparison.GameVersion && comparison.Differences.Count > 0);
    }

    public Task<IReadOnlyList<PlaysetSnapshot>> ListPlaysetSnapshotsAsync(CancellationToken cancellationToken = default) =>
        playsetSnapshots?.ListAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<PlaysetSnapshot>>([]);

    public async Task<PlaysetSnapshot> CapturePlaysetSnapshotAsync(AppSettings settings, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        var snapshots = playsetSnapshots ?? throw new NotSupportedException(T("PlaysetSnapshotsAreUnavailableForThisGame"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        ValidateEnabledMods(profile, scan.Mods);
        return await snapshots.CaptureAsync(environment, profile, scan.Mods, cancellationToken);
    }

    public async Task DeletePlaysetSnapshotAsync(AppSettings settings, Guid id,
        CancellationToken cancellationToken = default)
    {
        var snapshots = playsetSnapshots ?? throw new NotSupportedException(T("PlaysetSnapshotsAreUnavailableForThisGame"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        await snapshots.DeleteAsync(environment, id, cancellationToken);
    }

    public async Task<ModProfile> RestorePlaysetSnapshotAsync(AppSettings settings, Guid id,
        CancellationToken cancellationToken = default)
    {
        var snapshots = playsetSnapshots ?? throw new NotSupportedException(T("PlaysetSnapshotsAreUnavailableForThisGame"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var profile = await snapshots.RestoreAsync(environment, id, cancellationToken);
        await SaveProfileAsync(profile, cancellationToken);
        return profile;
    }

    public Task<ModProfile?> ReadGameProfileAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        ReadGameProfileAsync(settings, null, cancellationToken);

    public async Task<ModProfile?> ReadGameProfileAsync(AppSettings settings, ModProfile? intendedProfile, CancellationToken cancellationToken = default)
    {
        if (gameModActivator is null) throw new NotSupportedException(T("LauncherProfileWritesAreUnavailableForThis"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        if (intendedProfile is not null)
        {
            if (playsetSnapshots is not null)
                await playsetSnapshots.ValidateGameVersionAsync(environment, intendedProfile.Id, cancellationToken);
            ValidateEnabledMods(intendedProfile, scan.Mods);
        }
        return await gameModActivator.ReadAsync(environment, scan.Mods, cancellationToken);
    }

    public async Task<GameProfileApplyResult> ApplyGameProfileAsync(AppSettings settings, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (gameModActivator is null) throw new NotSupportedException(T("LauncherProfileWritesAreUnavailableForThis"));
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException(T("EnterAProfileNameFirst"));
        if (profile.Mods.Select(mod => mod.InstalledModId).Distinct().Count() != profile.Mods.Count)
            throw new InvalidOperationException(T("TheProfileContainsDuplicateMods"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        if (playsetSnapshots is not null)
            await playsetSnapshots.ValidateGameVersionAsync(environment, profile.Id, cancellationToken);
        ValidateEnabledMods(profile, scan.Mods);
        return await gameModActivator.ApplyAsync(environment, profile, scan.Mods, cancellationToken);
    }

    public async Task<GameProfilePreparation> ApplyAndVerifyGameProfileAsync(AppSettings settings, ModProfile profile,
        CancellationToken token)
    {
        var result = await ApplyGameProfileAsync(settings, profile, token);
        var actual = await ReadGameProfileAsync(settings, profile, token);
        token.ThrowIfCancellationRequested();
        if (!MatchesGameProfile(profile, actual))
            throw new InvalidOperationException(T("TheLauncherDoesNotMatchTheEnabled"));

        try { await SaveProfileAsync(profile, token); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(result, error); }
        return new(result);
    }

    public async Task<GameProfilePreparation> PrepareGameLaunchAsync(AppSettings settings, ModProfile profile,
        bool applyPendingChanges, CancellationToken token)
    {
        var actual = await ReadGameProfileAsync(settings, profile, token);
        token.ThrowIfCancellationRequested();
        if (!applyPendingChanges && MatchesGameProfile(profile, actual)) return new(null);
        return await ApplyAndVerifyGameProfileAsync(settings, profile, token);
    }

    public static bool MatchesGameProfile(ModProfile intended, ModProfile? actual) =>
        actual is not null && intended.Name.Trim() == actual.Name &&
        intended.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId)
            .SequenceEqual(actual.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId));

    private static void ValidateEnabledMods(ModProfile profile, IReadOnlyList<InstalledMod> installed)
    {
        foreach (var mod in profile.Mods.Where(mod => mod.Enabled))
            if (!installed.Any(found => found.Id == mod.InstalledModId && found.IsAvailable))
                throw new InvalidOperationException(T("ModFilesAreMissingDownloadOrDisable", mod.DisplayName));
    }

    public async Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return await RefreshAsync(settings, cancellationToken);
    }

    public async Task<WorkspaceState> RefreshAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var (environment, scan) = await DiscoverAndScanAsync(settings, cancellationToken);
        var profiles = await profileStore.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new WorkspaceState(settings, environment, scan, profiles);
    }

    public async Task<ModScanResult> ScanLocalModsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var (environment, scan) = await DiscoverAndScanAsync(settings, cancellationToken);
        return scan with { Environment = environment, ScanStartedAt = started };
    }

    private async Task<(GameEnvironment Environment, ModScanResult Scan)> DiscoverAndScanAsync(
        AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var scan = await modScanner.ScanAsync(environment, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (environment, scan);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        return settingsStore.SaveAsync(settings, cancellationToken);
    }

    public Task SaveProfileAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException(T("TheProfileNameCannotBeEmpty"), nameof(profile));
        if (profile.Mods is null) throw new ArgumentException(T("TheProfileNeedsAModList"), nameof(profile));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var workshopIds = new HashSet<ulong>();
        foreach (var mod in profile.Mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.InstalledModId))
                throw new ArgumentException(T("ProfileModIDsCannotBeEmpty"), nameof(profile));
            if (!ids.Add(mod.InstalledModId)) throw new ArgumentException(T("TheSameModCannotAppearTwiceIn"), nameof(profile));
            if (mod.WorkshopId is { } workshopId && !workshopIds.Add(workshopId))
                throw new ArgumentException(T("AProfileCannotContainTwoLocalCopies"), nameof(profile));
        }
        // 顺序由用户决定；保存时不排序、不推测加载优先级。
        return profileStore.SaveAsync(profile, cancellationToken);
    }

    public Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return profileStore.DeleteAsync(id, cancellationToken);
    }
}
