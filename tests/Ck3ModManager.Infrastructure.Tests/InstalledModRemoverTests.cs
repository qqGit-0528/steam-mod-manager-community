using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class InstalledModRemoverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("installed-mod-removal-").FullName;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalInstallAndItsAliasesArePhysicallyDeleted(bool archive)
    {
        var mods = Directory.CreateDirectory(Path.Combine(_root, "mod")).FullName;
        var name = archive ? "target.zip" : "target";
        var target = Path.Combine(mods, name);
        if (archive) await File.WriteAllTextAsync(target, "archive bytes", Token);
        else Directory.CreateDirectory(target);
        var field = archive ? "archive" : "path";
        var first = Path.Combine(mods, "first.mod");
        var second = Path.Combine(mods, "second.mod");
        await File.WriteAllTextAsync(first, $"name=\"Target\" {field}=\"mod/{name}\" remote_file_id=\"101\"", Token);
        await File.WriteAllTextAsync(second, $"name=\"Alias\" {field}=\"{target.Replace('\\', '/')}\"", Token);
        var neighbor = Path.Combine(mods, "other.mod");
        await File.WriteAllTextAsync(neighbor, "name=\"Other\" path=\"mod/other\"", Token);
        Directory.CreateDirectory(Path.Combine(mods, "other"));
        var environment = new GameEnvironment(null, _root, null, [], []);
        var scanner = new Ck3ModScanner();
        var selected = Assert.Single((await scanner.ScanAsync(environment, Token)).Mods, m => m.ContentPath == target);
        Assert.Equal(101UL, selected.WorkshopId);
        Assert.Equal(ModOrigin.SteamWorkshop, selected.Origin);
        Assert.Equal(ModInstallManagement.Local, selected.Management);
        var remover = new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(_root),
            (_, _, _) => throw new InvalidOperationException("本地副本不应取消订阅。"));

        await remover.RemoveAsync(environment, selected, Token);

        Assert.False(Path.Exists(target));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(neighbor));
        Assert.Equal("Other", Assert.Single((await scanner.ScanAsync(environment, Token)).Mods).Name);
    }

    [Fact]
    public async Task WorkshopFilesAreKeptUntilSteamConfirmsAndOnlyTargetIsDeleted()
    {
        var workshop = Directory.CreateDirectory(Path.Combine(_root, "steamapps", "workshop", "content", Ck3.AppId.ToString())).FullName;
        var target = Directory.CreateDirectory(Path.Combine(workshop, "101")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(workshop, "202")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "descriptor.mod"), "name=\"Target\"", Token);
        await File.WriteAllTextAsync(Path.Combine(other, "descriptor.mod"), "name=\"Other\"", Token);
        var environment = new GameEnvironment(null, _root, null, [workshop], []);
        var selected = Assert.Single((await new Ck3ModScanner().ScanAsync(environment, Token)).Mods, mod => mod.WorkshopId == 101);
        Assert.Equal(ModInstallManagement.Steam, selected.Management);
        var store = new FileWorkshopDownloadStore(_root);
        var confirmed = false;
        var calls = 0;
        var remover = new InstalledModRemover(Ck3.AppId, store, (_, id, _) =>
        {
            Assert.Equal(101UL, id);
            Assert.True(Directory.Exists(target));
            calls++;
            return confirmed ? Task.CompletedTask : Task.FromException(new IOException("Steam 拒绝取消订阅"));
        });

        await Assert.ThrowsAsync<IOException>(() => remover.RemoveAsync(environment, selected, Token));
        Assert.True(File.Exists(Path.Combine(target, "descriptor.mod")));
        using (await store.AcquireAsync(Ck3.AppId, 101, Token))
            await Assert.ThrowsAsync<IOException>(() => remover.RemoveAsync(environment, selected, Token));
        Assert.Equal(1, calls);

        confirmed = true;
        await remover.RemoveAsync(environment, selected, Token);
        Assert.Equal(2, calls);
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(other, "descriptor.mod")));
    }

    [Fact]
    public async Task DescriptorCannotDeleteContentOutsideManagedModDirectories()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_root, "mod")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var wrapper = Path.Combine(mods, "outside.mod");
        await File.WriteAllTextAsync(wrapper, $"name=\"Outside\" path=\"{outside.Replace('\\', '/')}\"", Token);
        var environment = new GameEnvironment(null, _root, null, [], []);
        var selected = Assert.Single((await new Ck3ModScanner().ScanAsync(environment, Token)).Mods);
        var remover = new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(_root), null);
        Assert.Equal(ModInstallManagement.External, selected.Management);

        await Assert.ThrowsAsync<InvalidOperationException>(() => remover.RemoveAsync(environment, selected, Token));
        Assert.True(Directory.Exists(outside));
        Assert.True(File.Exists(wrapper));
    }

    [Fact]
    public async Task SymbolicLinkIsRejectedBeforeItsTargetCanBeDeleted()
    {
        if (OperatingSystem.IsWindows()) return;
        var mods = Directory.CreateDirectory(Path.Combine(_root, "mod")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(mods, "target");
        Directory.CreateSymbolicLink(link, outside);
        var wrapper = Path.Combine(mods, "target.mod");
        await File.WriteAllTextAsync(wrapper, "name=\"Target\" path=\"mod/target\"", Token);
        var environment = new GameEnvironment(null, _root, null, [], []);
        var selected = Assert.Single((await new Ck3ModScanner().ScanAsync(environment, Token)).Mods);
        var remover = new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(_root), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => remover.RemoveAsync(environment, selected, Token));
        Assert.True(Directory.Exists(outside));
        Assert.True(File.Exists(wrapper));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
