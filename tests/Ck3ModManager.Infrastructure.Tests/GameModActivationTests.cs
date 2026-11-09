using System.IO.Compression;
using System.Security.Cryptography;
using Ck3ModManager.Core;
using Microsoft.Data.Sqlite;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class GameModActivationTests
{
    [Fact]
    public async Task ApplyCreatesSeparatePlaysetWithTogglesOrderAndDlcSettingsAndCanBeReadBack()
    {
        using var fixture = new Fixture();
        var activator = fixture.Activator();
        var original = await activator.ReadAsync(fixture.Environment, fixture.Mods, TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        Assert.Equal(new[] { true, false }, original.Mods.Select(mod => mod.Enabled));
        var desired = original with { Mods = [original.Mods[1] with { Enabled = true }, original.Mods[0] with { Enabled = false }] };

        var result = await activator.ApplyAsync(fixture.Environment, desired, fixture.Mods, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EnabledCount);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal("1", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'", result.BackupPath));
        Assert.Equal("0", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'"));
        Assert.Equal("2", fixture.Scalar("SELECT COUNT(*) FROM playsets_mods WHERE playsetId='original'"));
        Assert.Equal("1", fixture.Scalar("SELECT COUNT(*) FROM playsets_dlcs d JOIN playsets p ON d.playsetId=p.id WHERE p.isActive=1 AND d.dlcId='paid-dlc' AND d.enabled=0"));
        var read = await activator.ReadAsync(fixture.Environment, fixture.Mods, TestContext.Current.CancellationToken);
        Assert.Equal(desired.Mods.Select(mod => (mod.InstalledModId, mod.Enabled)), read!.Mods.Select(mod => (mod.InstalledModId, mod.Enabled)));
        Assert.Equal("original-game-json", File.ReadAllText(Path.Combine(fixture.User, "dlc_load.json")));

        await activator.ApplyAsync(fixture.Environment, desired, fixture.Mods, TestContext.Current.CancellationToken);
        Assert.Equal("2", fixture.Scalar("SELECT COUNT(*) FROM playsets"));
        Assert.Equal("1", fixture.Scalar("SELECT COUNT(*) FROM playsets WHERE isActive=1"));
    }

    [Fact]
    public async Task LauncherOpeningDuringApplyRollsBackAndRemovesOnlyNewWrappers()
    {
        using var fixture = new Fixture(registerMods: false);
        var checks = 0;
        var activator = fixture.Activator(_ => { if (++checks == 2) throw new InvalidOperationException("启动器已打开"); });
        var profile = new ModProfile(Guid.NewGuid(), "测试", null, fixture.Mods.Select(mod => new ProfileMod(mod.Id, mod.Name, mod.ContentPath, null, null)).ToArray(), DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => activator.ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken));

        Assert.Equal("0", fixture.Scalar("SELECT COUNT(*) FROM mods"));
        Assert.Equal("1", fixture.Scalar("SELECT COUNT(*) FROM playsets"));
        Assert.Equal("1", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'"));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.User, "mod"), "app_mm_*.mod"));
        Assert.True(File.Exists(fixture.Mods[0].DescriptorPath));
    }

    [Fact]
    public async Task NewSteamContentGetsARealOuterDescriptorWithoutChangingTheOriginal()
    {
        using var fixture = new Fixture(registerMods: false);
        var mod = fixture.Mods[0];
        var before = File.ReadAllText(mod.DescriptorPath);
        var profile = new ModProfile(Guid.NewGuid(), "新下载", null, [new ProfileMod(mod.Id, mod.Name, mod.ContentPath, 123, "1.20.*")], DateTimeOffset.UtcNow);
        await fixture.Activator().ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken);
        var wrapper = Assert.Single(Directory.GetFiles(Path.Combine(fixture.User, "mod"), "app_mm_*.mod"));
        var scanned = await new Ck3ModScanner().ScanAsync(fixture.User, [], TestContext.Current.CancellationToken);
        Assert.Contains(scanned.Mods, installed => installed.DescriptorPath == wrapper && installed.ContentPath == mod.ContentPath);
        Assert.Equal(before, File.ReadAllText(mod.DescriptorPath));
        Assert.Equal("local", fixture.Scalar("SELECT source FROM mods"));
        Assert.StartsWith("mod/app_mm_", fixture.Scalar("SELECT gameRegistryId FROM mods"));
        Assert.Equal("integer", fixture.Scalar("SELECT typeof(createdDate) FROM mods"));
        Assert.Equal("integer", fixture.Scalar("SELECT typeof(createdOn) FROM playsets WHERE isActive=1"));
    }

    [Fact]
    public async Task SavedDisabledStateRoundTripsAndOlderProfilesDefaultToEnabled()
    {
        using var fixture = new Fixture();
        var store = new JsonProfileStore(fixture.Root);
        var mod = fixture.Mods[0];
        var profile = new ModProfile(Guid.NewGuid(), "兼容", null,
            [new ProfileMod(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, null) { Enabled = false }], DateTimeOffset.UtcNow);
        await store.SaveAsync(profile, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)).Mods).Enabled);
        var path = Path.Combine(fixture.Root, "profiles.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        json[0]!["mods"]![0]!.AsObject().Remove("enabled");
        File.WriteAllText(path, json.ToJsonString());
        Assert.True(Assert.Single(Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)).Mods).Enabled);
    }

    [Fact]
    public async Task UnknownSchemaAndWrongGameAreRejectedBeforeModifyingTheDatabase()
    {
        using var fixture = new Fixture();
        var profile = new ModProfile(Guid.NewGuid(), "测试", null, [], DateTimeOffset.UtcNow);
        var wrongGame = new ParadoxGameModActivator(GameCatalog.Hoi4, fixture.Root, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongGame.ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken));
        fixture.Execute("ALTER TABLE playsets_mods RENAME COLUMN position TO unknownOrder");
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Activator().ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken));
        Assert.Equal("1", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'"));
    }

    [Fact]
    public async Task BusyLauncherPreventsEvenTheBackupAndHistoryIsBounded()
    {
        using var fixture = new Fixture();
        var profile = new ModProfile(Guid.NewGuid(), "测试", null, [], DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Activator(_ => throw new InvalidOperationException("busy"))
            .ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "activation-backups")));
        var activator = fixture.Activator();
        for (var i = 0; i < 7; i++) await activator.ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken);
        Assert.Equal(5, Directory.GetFiles(Path.Combine(fixture.Root, "activation-backups"), "*.sqlite").Length);
    }

    [Fact]
    public async Task RepeatedFailedWritesAlsoKeepAtMostFiveBackups()
    {
        using var fixture = new Fixture();
        var profile = new ModProfile(Guid.NewGuid(), "测试", null, [], DateTimeOffset.UtcNow);
        for (var i = 0; i < 7; i++)
        {
            var checks = 0;
            var activator = fixture.Activator(_ => { if (++checks == 2) throw new InvalidOperationException("busy"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => activator.ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken));
        }
        Assert.Equal(5, Directory.GetFiles(Path.Combine(fixture.Root, "activation-backups"), "*.sqlite").Length);
        Assert.Equal("1", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'"));
    }

    [Fact]
    public async Task VictoriaMetadataRequiresNativeDiscoverableDirectoryForNewLocalRegistration()
    {
        using var fixture = new Fixture(game: GameCatalog.Victoria3, registerMods: false);
        var scan = await new Ck3ModScanner().ScanAsync(fixture.User, [fixture.Content], TestContext.Current.CancellationToken);
        Assert.Equal(2, scan.Mods.Count);
        Assert.Empty(scan.Warnings);
        Assert.All(scan.Mods, mod => Assert.Equal("1.13.*", mod.SupportedGameVersion));
        var external = new ModProfile(Guid.NewGuid(), "Victoria", null,
            scan.Mods.Select(mod => new ProfileMod(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, mod.SupportedGameVersion)).ToArray(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Activator().ApplyAsync(fixture.Environment, external, scan.Mods, TestContext.Current.CancellationToken));
        var disabled = external with { Mods = external.Mods.Select(mod => mod with { Enabled = false }).ToArray() };
        var disabledResult = await fixture.Activator().ApplyAsync(fixture.Environment, disabled, scan.Mods, TestContext.Current.CancellationToken);
        Assert.Equal(0, disabledResult.EnabledCount);
        Assert.Equal("0", fixture.Scalar("SELECT COUNT(*) FROM mods"));
        var native = Path.Combine(fixture.User, "mod", "local", ".metadata");
        Directory.CreateDirectory(native);
        File.Copy(scan.Mods[0].DescriptorPath, Path.Combine(native, "metadata.json"));
        scan = await new Ck3ModScanner().ScanAsync(fixture.User, [], TestContext.Current.CancellationToken);
        var local = Assert.Single(scan.Mods);
        var profile = external with { Mods = [new ProfileMod(local.Id, local.Name, local.ContentPath, null, local.SupportedGameVersion)] };
        await fixture.Activator().ApplyAsync(fixture.Environment, profile, scan.Mods, TestContext.Current.CancellationToken);
        Assert.Equal("1", fixture.Scalar("SELECT COUNT(*) FROM mods WHERE dirPath IS NOT NULL AND gameRegistryId IS NULL"));
        Assert.False(File.Exists(Path.Combine(fixture.User, "content_load.json")));
        var current = await fixture.Activator().ReadAsync(fixture.Environment, scan.Mods, TestContext.Current.CancellationToken);
        Assert.Single(current!.Mods);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task InvalidMetadataRootDoesNotBreakTheRestOfTheLibrary(string invalid)
    {
        using var fixture = new Fixture(game: GameCatalog.Victoria3, registerMods: false);
        var bad = Path.Combine(fixture.User, "mod", "bad", ".metadata"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "metadata.json"), invalid);
        var result = await new Ck3ModScanner().ScanAsync(fixture.User, [fixture.Content], TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Mods.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("metadata.json"));
    }

    [Fact]
    public async Task ImportMergesDuplicateNativeEntriesAndPreservesAnEnabledOccurrence()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO playsets_mods VALUES('original','mod1',0,2)");
        var profile = await fixture.Activator().ReadAsync(fixture.Environment, fixture.Mods, TestContext.Current.CancellationToken);
        Assert.Equal(2, profile!.Mods.Count);
        Assert.True(profile.Mods[0].Enabled);
    }

    [Fact]
    public async Task GeneratedWrapperUsesTheActualWorkshopIdentityInsteadOfTheAuthorsOldId()
    {
        using var fixture = new Fixture(registerMods: false);
        var mod = fixture.Mods[0];
        File.AppendAllText(mod.DescriptorPath, "remote_file_id=999\ntags={ \"Gameplay\" }\n");
        var profile = new ModProfile(Guid.NewGuid(), "来源", null, [new ProfileMod(mod.Id, mod.Name, mod.ContentPath, 1, null)], DateTimeOffset.UtcNow);
        await fixture.Activator().ApplyAsync(fixture.Environment, profile, fixture.Mods, TestContext.Current.CancellationToken);
        var scan = await new Ck3ModScanner().ScanAsync(fixture.User, [fixture.Content], TestContext.Current.CancellationToken);
        Assert.Equal(1UL, Assert.Single(scan.Mods, found => found.ContentPath == mod.ContentPath).WorkshopId);
        Assert.Contains("999", File.ReadAllText(mod.DescriptorPath));
    }

    [Fact]
    public async Task WorkshopUpdateCanBeRestoredAsSeparateLocalContentAndAppliedInOriginalOrder()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var first = fixture.Mods[0];
        var workshopAsset = Path.Combine(first.ContentPath, "asset.txt");
        File.WriteAllText(workshopAsset, "original workshop content");
        File.AppendAllText(first.DescriptorPath, "remote_file_id=1\n");
        var archive = Path.Combine(fixture.Root, "local.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        using (var output = new StreamWriter(zip.CreateEntry("mod.txt").Open()))
            output.Write("original archive");
        var archiveBytes = File.ReadAllBytes(archive);
        var archiveMod = fixture.Mods[1] with { ContentPath = archive, WorkshopId = null, Origin = ModOrigin.Local };
        var profile = new ModProfile(Guid.NewGuid(), "可用播放集", "目标版本",
            [new(archiveMod.Id, archiveMod.Name, archive, null, null),
             new("disabled", "尚未安装的禁用项", "", 456, null) { Enabled = false },
             new(first.Id, first.Name, first.ContentPath, first.WorkshopId, first.SupportedGameVersion)], DateTimeOffset.UtcNow);
        var store = new Ck3PlaysetSnapshotStore(fixture.Root);

        var saved = await store.CaptureAsync(fixture.Environment, profile, [first, archiveMod], token);
        Assert.Equal("1.20.1", saved.GameVersion);
        Assert.Equal(2, saved.EnabledCount);
        Assert.Equal(saved, Assert.Single(await new Ck3PlaysetSnapshotStore(fixture.Root).ListAsync(token)));
        Assert.Empty((await store.CompareAsync(fixture.Environment, saved.Id, profile, [first, archiveMod], token)).Differences);
        var reversed = profile with { Mods = profile.Mods.Reverse().ToArray() };
        Assert.Contains((await store.CompareAsync(fixture.Environment, saved.Id, reversed, [first, archiveMod], token)).Differences,
            message => message.Contains("加载顺序", StringComparison.Ordinal));
        File.WriteAllText(workshopAsset, "Steam update replaced the files");
        Assert.Contains((await store.CompareAsync(fixture.Environment, saved.Id, profile, [first, archiveMod], token)).Differences,
            message => message.Contains("文件或描述内容不同：Mod 1", StringComparison.Ordinal));
        File.Delete(archive);

        var restored = await store.RestoreAsync(fixture.Environment, saved.Id, token);
        var repeated = await store.RestoreAsync(fixture.Environment, saved.Id, token);
        Assert.Equal(restored.Id, repeated.Id);
        Assert.Equal(restored.Mods, repeated.Mods);
        Assert.NotEqual(profile.Id, restored.Id);
        Assert.Equal(new[] { "Mod 2", "尚未安装的禁用项", "Mod 1" }, restored.Mods.Select(mod => mod.DisplayName));
        Assert.Equal(new[] { true, false, true }, restored.Mods.Select(mod => mod.Enabled));
        Assert.Equal(archiveBytes, File.ReadAllBytes(restored.Mods[0].ContentPath));
        Assert.Equal("original workshop content", File.ReadAllText(Path.Combine(restored.Mods[2].ContentPath, "asset.txt")));
        Assert.Equal("Steam update replaced the files", File.ReadAllText(workshopAsset));
        Assert.All(restored.Mods, mod => Assert.Null(mod.WorkshopId));

        var scan = await new Ck3ModScanner().ScanAsync(fixture.User, [fixture.Content], token);
        Assert.Empty(scan.Warnings);
        Assert.Empty((await store.CompareAsync(fixture.Environment, saved.Id, restored, scan.Mods, token)).Differences);
        var restoredMod = Assert.Single(scan.Mods, mod => mod.Id == restored.Mods[2].InstalledModId);
        Assert.Equal(ModOrigin.Local, restoredMod.Origin);
        Assert.Contains("replace_path=\"common/test\"", File.ReadAllText(restoredMod.DescriptorPath));
        Assert.DoesNotContain("remote_file_id", File.ReadAllText(restoredMod.DescriptorPath));
        var activator = fixture.Activator();
        await activator.ApplyAsync(fixture.Environment, restored, scan.Mods, token);
        var active = await activator.ReadAsync(fixture.Environment, scan.Mods, token);
        Assert.Equal(restored.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId),
            active!.Mods.Select(mod => mod.InstalledModId));
        Assert.Equal("2", fixture.Scalar("SELECT COUNT(*) FROM playsets_mods WHERE playsetId='original'"));
        Assert.Equal("2", fixture.Scalar("SELECT COUNT(*) FROM mods WHERE source='local'"));

        var deployedAsset = Path.Combine(restored.Mods[2].ContentPath, "asset.txt");
        File.WriteAllText(deployedAsset, "manual edit");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(fixture.Environment, saved.Id, token));
        Assert.Contains("已恢复的 Mod 文件有变化", error.Message);
        Assert.Equal("manual edit", File.ReadAllText(deployedAsset));
        var storedAsset = Path.Combine(fixture.Root, "playset-snapshots", saved.Id.ToString("N"), "0002", "content", "asset.txt");
        Assert.Equal("original workshop content", File.ReadAllText(storedAsset));
    }

    [Fact]
    public async Task DeletingSnapshotRemovesItsContentAndRestoredCopies()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var mod = fixture.Mods[0];
        var profile = new ModProfile(Guid.NewGuid(), "待删除快照", null,
            [new(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, mod.SupportedGameVersion)], DateTimeOffset.UtcNow);
        var store = new Ck3PlaysetSnapshotStore(fixture.Root);
        var saved = await store.CaptureAsync(fixture.Environment, profile, fixture.Mods, token);
        await store.RestoreAsync(fixture.Environment, saved.Id, token);

        var local = "app_mm_snapshot_" + saved.Id.ToString("N");
        var modRoot = Path.Combine(fixture.User, "mod");
        var restoredRoot = Path.Combine(modRoot, local);
        Assert.True(Directory.Exists(restoredRoot));
        Assert.NotEmpty(Directory.EnumerateFiles(modRoot, local + "_*.mod"));

        await store.DeleteAsync(fixture.Environment, saved.Id, token);

        Assert.False(Directory.Exists(restoredRoot));
        Assert.Empty(Directory.EnumerateFiles(modRoot, local + "_*.mod"));
        Assert.Empty(await store.ListAsync(token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "playset-snapshots", saved.Id.ToString("N"))));
    }

    [Fact]
    public async Task ChangedGameVersionAndCorruptSnapshotFailBeforePublishingLocalMods()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var mod = fixture.Mods[0];
        var profile = new ModProfile(Guid.NewGuid(), "原配置", null,
            [new(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, mod.SupportedGameVersion)], DateTimeOffset.UtcNow);
        var store = new Ck3PlaysetSnapshotStore(fixture.Root);
        var saved = await store.CaptureAsync(fixture.Environment, profile, fixture.Mods, token);

        var newer = fixture.Environment with { GameVersion = "1.21.0" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(newer, saved.Id, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ValidateGameVersionAsync(newer, saved.Id, token));
        var copy = Path.Combine(fixture.Root, "playset-snapshots", saved.Id.ToString("N"), "0000", "content", "descriptor.mod");
        File.AppendAllText(copy, "# tampered");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(fixture.Environment, saved.Id, token));
        Assert.Contains("Mod 文件校验失败", error.Message);
        var modRoot = Path.Combine(fixture.User, "mod");
        Assert.Empty(Directory.GetFiles(modRoot, "*.mod"));
        Assert.Empty(Directory.GetDirectories(modRoot));
        Assert.Equal("1", fixture.Scalar("SELECT isActive FROM playsets WHERE id='original'"));
    }

    [Fact]
    public async Task LinkedContentIsRejectedWithoutPublishingAPartialSnapshot()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var mod = fixture.Mods[0];
        File.CreateSymbolicLink(Path.Combine(mod.ContentPath, "outside.txt"), fixture.Mods[1].DescriptorPath);
        var profile = new ModProfile(Guid.NewGuid(), "带链接", null,
            [new(mod.Id, mod.Name, mod.ContentPath, mod.WorkshopId, mod.SupportedGameVersion)], DateTimeOffset.UtcNow);
        var store = new Ck3PlaysetSnapshotStore(fixture.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CaptureAsync(fixture.Environment, profile, fixture.Mods, token));
        Assert.Empty(await store.ListAsync(token));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "playset-snapshots")));
    }

    [Fact]
    public async Task PlaysetPackageRestoresExactPayloadAndOrderIntoAnIndependentWorkspace()
    {
        using var sender = new Fixture();
        using var receiver = new Fixture(registerMods: false);
        var token = TestContext.Current.CancellationToken;
        var archivePath = Path.Combine(sender.Root, "old.zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var output = new StreamWriter(zip.CreateEntry("common/old.txt").Open()))
            output.Write("old archived asset");

        var a = sender.Mods[0];
        var b = sender.Mods[1] with { ContentPath = archivePath, WorkshopId = null };
        var aFile = Path.Combine(a.ContentPath, "old.txt");
        File.WriteAllText(aFile, "old asset");
        Directory.CreateDirectory(Path.Combine(a.ContentPath, "empty"));
        var beforeA = SHA256.HashData(File.ReadAllBytes(aFile));
        var beforeB = SHA256.HashData(File.ReadAllBytes(archivePath));
        var profile = new ModProfile(Guid.NewGuid(), "跨机配置", null,
            [new(b.Id, b.Name, b.ContentPath, null, null),
             new("disabled", "禁用项", "", 500, null, "3") { Enabled = false },
             new(a.Id, a.Name, a.ContentPath, a.WorkshopId, null)], DateTimeOffset.UtcNow);
        var path = Path.Combine(sender.Root, "shared.ck3pack");
        var producer = new Ck3PlaysetSnapshotStore(sender.Root);
        await producer.ExportPackageAsync(sender.Environment, profile, [a, b], path, token);
        Assert.Empty(await producer.ListAsync(token));

        File.WriteAllText(aFile, "updated after export");
        File.Delete(archivePath);
        var store = new Ck3PlaysetSnapshotStore(receiver.Root);
        var preview = await store.PreviewPackageAsync(path, token);
        Assert.Equal("1.20.1", preview.GameVersion);
        Assert.Equal(new[] { "Mod 2", "禁用项", "Mod 1" }, preview.Mods.Select(mod => mod.Name));
        Assert.Equal(new[] { true, false, true }, preview.Mods.Select(mod => mod.Enabled));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(receiver.User, "mod")));

        var imported = await store.ImportPackageAsync(receiver.Environment, preview, token);
        Assert.NotEqual(profile.Id, imported.Id);
        Assert.All(imported.Mods.Where(mod => mod.Enabled), mod => Assert.StartsWith(receiver.User, mod.ContentPath));
        Assert.Equal(new[] { true, false, true }, imported.Mods.Select(mod => mod.Enabled));
        Assert.Equal("3", imported.Mods[1].ModVersion);
        Assert.Equal(beforeB, SHA256.HashData(File.ReadAllBytes(imported.Mods[0].ContentPath)));
        Assert.Equal(beforeA, SHA256.HashData(File.ReadAllBytes(Path.Combine(imported.Mods[2].ContentPath, "old.txt"))));
        Assert.True(Directory.Exists(Path.Combine(imported.Mods[2].ContentPath, "empty")));
        Assert.Empty(await store.ListAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ValidateGameVersionAsync(receiver.Environment with { GameVersion = "1.21.0" }, imported.Id, token));

        var scan = await new Ck3ModScanner().ScanAsync(receiver.Environment, token);
        var restoredMods = scan.Mods.Where(mod => imported.Mods.Any(item => item.InstalledModId == mod.Id)).ToArray();
        Assert.Equal(2, restoredMods.Length);
        Assert.All(restoredMods, mod =>
        {
            Assert.Null(mod.WorkshopId);
            Assert.Equal(ModInstallManagement.Local, mod.Management);
        });
        var activator = receiver.Activator();
        await activator.ApplyAsync(receiver.Environment, imported, scan.Mods, token);
        var readback = await activator.ReadAsync(receiver.Environment, scan.Mods, token);
        Assert.Equal(imported.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId),
            readback!.Mods.Select(mod => mod.InstalledModId));

        var removed = Assert.Single(restoredMods, mod => mod.Id == imported.Mods[0].InstalledModId);
        var remover = new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(receiver.Root), null);
        await remover.RemoveAsync(receiver.Environment, removed, token);
        Assert.False(File.Exists(removed.ContentPath));
        Assert.False(File.Exists(removed.DescriptorPath));
        Assert.True(Directory.Exists(imported.Mods[2].ContentPath));
    }

    [Fact]
    public async Task IncompatibleOrTamperedPackageLeavesTheReceiverUnchanged()
    {
        using var sender = new Fixture();
        using var receiver = new Fixture(registerMods: false);
        var token = TestContext.Current.CancellationToken;
        var source = sender.Mods[0];
        File.WriteAllText(Path.Combine(source.ContentPath, "asset.txt"), "expected");
        var profile = new ModProfile(Guid.NewGuid(), "配置", null,
            [new(source.Id, source.Name, source.ContentPath, source.WorkshopId, null)], DateTimeOffset.UtcNow);
        var path = Path.Combine(sender.Root, "shared.ck3pack");
        await new Ck3PlaysetSnapshotStore(sender.Root).ExportPackageAsync(sender.Environment, profile, [source], path, token);
        var store = new Ck3PlaysetSnapshotStore(receiver.Root);
        var preview = await store.PreviewPackageAsync(path, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ImportPackageAsync(receiver.Environment with { GameVersion = "1.21.0" }, preview, token));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("0000/content/asset.txt")!.Delete();
            using var output = new StreamWriter(archive.CreateEntry("0000/content/asset.txt").Open());
            output.Write("tampered");
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportPackageAsync(receiver.Environment, preview, token));
        Assert.Contains("校验失败", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(receiver.User, "mod")));
        Assert.Equal("original-game-json", File.ReadAllText(Path.Combine(receiver.User, "dlc_load.json")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mod-activation-" + Guid.NewGuid().ToString("N"));
        public string User => Path.Combine(Root, "user");
        public string Content => Path.Combine(Root, "workshop");
        private string Database => Path.Combine(User, "launcher-v2.sqlite");
        private readonly GameDefinition _game;
        public GameEnvironment Environment { get; }
        public InstalledMod[] Mods { get; }

        public Fixture(bool registerMods = true, GameDefinition? game = null)
        {
            _game = game ?? GameCatalog.Ck3;
            var install = Path.Combine(Root, "game");
            Directory.CreateDirectory(Path.Combine(install, "launcher"));
            Directory.CreateDirectory(Path.Combine(User, "mod"));
            File.WriteAllText(Path.Combine(install, "launcher", "launcher-settings.json"),
                System.Text.Json.JsonSerializer.Serialize(new { gameId = _game.Id, formatVersion = _game.Id == "victoria3" ? 1.1 : 0 }));
            File.WriteAllText(Path.Combine(User, "dlc_load.json"), "original-game-json");
            Environment = new GameEnvironment(install, User, "1.20.1", [Content], []);
            Mods = Enumerable.Range(1, 2).Select(i =>
            {
                var folder = Path.Combine(Content, i.ToString()); Directory.CreateDirectory(folder);
                string descriptor;
                if (_game.Id == "victoria3")
                {
                    Directory.CreateDirectory(Path.Combine(folder, ".metadata"));
                    descriptor = Path.Combine(folder, ".metadata", "metadata.json");
                    File.WriteAllText(descriptor, "{\"name\":\"Mod " + i + "\",\"supported_game_version\":\"1.13.*\"}");
                }
                else { descriptor = Path.Combine(folder, "descriptor.mod"); File.WriteAllText(descriptor, $"name=\"Mod {i}\"\nsupported_version=\"1.20.*\"\nreplace_path=\"common/test\"\n"); }
                return new InstalledMod("mod" + i, "Mod " + i, folder, descriptor, "1", "1.20.*", (ulong)i, [], ModOrigin.SteamWorkshop, true);
            }).ToArray();
            Execute("""
                CREATE TABLE mods(id TEXT PRIMARY KEY,name TEXT,displayName TEXT,gameRegistryId TEXT,dirPath TEXT,archivePath TEXT,status TEXT NOT NULL,source TEXT NOT NULL,isNew INTEGER,createdDate datetime,requiredVersion TEXT,version TEXT);
                CREATE TABLE playsets(id TEXT PRIMARY KEY,name TEXT NOT NULL,isActive INTEGER,loadOrder TEXT,createdOn datetime NOT NULL,updatedOn datetime,isRemoved INTEGER NOT NULL DEFAULT 0,syncState TEXT,pdxId INTEGER,state TEXT DEFAULT 'private',owned INTEGER DEFAULT 1);
                CREATE TABLE playsets_mods(playsetId TEXT REFERENCES playsets(id),modId TEXT REFERENCES mods(id),enabled INTEGER,position INTEGER);
                CREATE TABLE playsets_dlcs(playsetId TEXT REFERENCES playsets(id),dlcId TEXT,enabled INTEGER);
                INSERT INTO playsets(id,name,isActive,loadOrder,createdOn) VALUES('original','原配置',1,'custom','2026-01-01');
                INSERT INTO playsets_dlcs VALUES('original','paid-dlc',0);
                """);
            if (registerMods)
            {
                for (var i = 0; i < Mods.Length; i++)
                {
                    using var db = Open(); using var command = db.CreateCommand();
                    command.CommandText = "INSERT INTO mods(id,name,gameRegistryId,dirPath,status,source) VALUES($id,$name,$registry,$path,'ready_to_play','steam'); INSERT INTO playsets_mods VALUES('original',$id,$enabled,$position);";
                    command.Parameters.AddWithValue("$id", Mods[i].Id); command.Parameters.AddWithValue("$name", Mods[i].Name);
                    command.Parameters.AddWithValue("$registry", "mod/ugc_" + (i + 1) + ".mod"); command.Parameters.AddWithValue("$path", Mods[i].ContentPath);
                    command.Parameters.AddWithValue("$enabled", i == 0); command.Parameters.AddWithValue("$position", i); command.ExecuteNonQuery();
                }
            }
        }

        public ParadoxGameModActivator Activator(Action<GameEnvironment>? guard = null) => new(_game, Root, guard ?? (_ => { }));
        private SqliteConnection Open(string? path = null) { var c = new SqliteConnection($"Data Source={path ?? Database};Pooling=False"); c.Open(); return c; }
        public string Scalar(string sql, string? path = null) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToString(q.ExecuteScalar())!; }
        public void Execute(string sql) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
