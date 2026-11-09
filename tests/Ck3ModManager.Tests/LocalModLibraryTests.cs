using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class LocalModLibraryTests
{
    private static readonly string Root = Path.GetFullPath("local-mod-library-fixture");

    [Fact]
    public void OlderScansCannotRestoreAnExplicitlyRemovedInstallAfterAPartialScan()
    {
        var library = new LocalModLibrary(null);
        var removed = Mod("removed", Path.Combine(Root, "removed"));
        var other = Mod("other", Path.Combine(Root, "other"));
        library.Accept(new([removed, other], []) { ScanStartedAt = 10 });
        var deleted = library.Accept(new([], ["部分目录暂不可读"]) { ScanStartedAt = 30 }, removedModId: removed.Id);

        Assert.NotNull(deleted);
        Assert.Equal(other, Assert.Single(deleted.Scan.Mods));
        Assert.Null(library.Accept(new([removed, other], []) { ScanStartedAt = 20 }));
        Assert.Equal(other, Assert.Single(library.Accept(new([], ["仍暂不可读"]) { ScanStartedAt = 40 })!.Scan.Mods));
    }

    [Fact]
    public void AnotherCopyOfTheSameWorkshopItemDoesNotRestoreTheHistoricalInstallation()
    {
        var library = new LocalModLibrary(new ContentProbe());
        var path = Path.Combine(Root, "removed");
        library.Accept(new([Mod("another", Path.Combine(Root, "another"))], []));
        var task = TaskAt(Ck3.AppId, path);
        Assert.Equal(LocalInstallationState.Missing, library.FindInstallation(task).State);

        library.Accept(new([Mod("reinstalled", path)], []));
        Assert.Equal(new LocalInstallation(LocalInstallationState.Installed, path), library.FindInstallation(task));
    }

    [Fact]
    public void LegacyContainerCanBeCheckedOnlyInsideTheMatchingGameWorkshopPath()
    {
        var probe = new ContentProbe();
        var library = new LocalModLibrary(probe);
        var workshop = Path.Combine(Root, "workshop");
        library.Accept(new([], []) { Environment = new GameEnvironment(null, Root, null, [workshop], []) });
        var appId = GameCatalog.PrisonArchitect.AppId;
        var container = Path.Combine(workshop, "101", "101_legacy.bin");
        Assert.Equal(LocalInstallationState.Installed, library.FindInstallation(TaskAt(appId, container)).State);
        Assert.True(probe.RequirePayload);
        Assert.Equal(LocalInstallationState.Missing, library.FindInstallation(TaskAt(appId, Path.Combine(Root, "101_legacy.bin"))).State);
        Assert.Equal(LocalInstallationState.Missing, library.FindInstallation(TaskAt(appId, Path.Combine(workshop, "202", "101_legacy.bin"))).State);
    }

    [Fact]
    public void DuplicateCopiesRequireDistinctPathsAndTrackAcceptedChanges()
    {
        var library = new LocalModLibrary(null);
        var steam = Mod("steam", Path.Combine(Root, "workshop", "101")) with { Management = ModInstallManagement.Steam };
        var local = Mod("local", Path.Combine(Root, "mod", "copy")) with { Management = ModInstallManagement.Local, ModVersion = "old" };
        var wrapper = steam with { Id = "another-wrapper" };
        var missing = local with { Id = "missing", ContentPath = Path.Combine(Root, "missing"), IsAvailable = false };
        library.Accept(new([steam, wrapper, local, missing], []) { ScanStartedAt = 20 });

        var copies = Assert.IsType<DuplicateModCopies>(library.FindDuplicateCopies(steam));
        Assert.True(copies.Confirmed);
        Assert.Equal(local, Assert.Single(copies.OtherCopies));
        Assert.Equal(steam, Assert.Single(library.FindDuplicateCopies(local)!.OtherCopies));
        Assert.Null(library.FindDuplicateCopies(missing));

        library.Accept(new([steam, wrapper, missing], []) { ScanStartedAt = 22 });
        Assert.Null(library.Accept(new([steam, local], []) { ScanStartedAt = 21 }));
        Assert.Null(library.FindDuplicateCopies(steam));
    }

    [Fact]
    public void SameNameIsSuspectedOnlyWhenAWorkshopIdentityIsMissing()
    {
        var library = new LocalModLibrary(null);
        var steam = Mod("steam", Path.Combine(Root, "steam")) with { Name = "A" };
        var another = Mod("another", Path.Combine(Root, "another")) with { Name = "A", WorkshopId = 202 };
        var unknown = Mod("unknown", Path.Combine(Root, "unknown")) with { Name = " a ", WorkshopId = null };
        library.Accept(new([steam, another], []));
        Assert.Null(library.FindDuplicateCopies(steam));

        library.Accept(new([steam, another, unknown], []));
        Assert.False(library.FindDuplicateCopies(steam)!.Confirmed);
        Assert.Equal(unknown, Assert.Single(library.FindDuplicateCopies(steam)!.OtherCopies));
        Assert.Equal(2, library.FindDuplicateCopies(unknown)!.OtherCopies.Count);
    }

    private static InstalledMod Mod(string id, string path) => new(id, id, path, Path.Combine(path, "descriptor.mod"),
        "1", "1.20", 101, [], ModOrigin.SteamWorkshop, true);

    private static WorkshopDownloadSnapshot TaskAt(uint appId, string path) =>
        new(Guid.NewGuid(), appId, 101, "Mod", WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow, InstalledPath: path)
        { Owner = WorkshopDownloadOwner.SteamClient };

    private sealed class ContentProbe : IInstalledModContent
    {
        public bool RequirePayload { get; private set; }
        public LocalInstallation Inspect(string path, bool requirePayload)
        {
            RequirePayload = requirePayload;
            return new(LocalInstallationState.Installed, path);
        }
    }
}
