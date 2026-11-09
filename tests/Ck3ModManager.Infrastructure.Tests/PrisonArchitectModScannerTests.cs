using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class PrisonArchitectModScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("prison-architect-mods-").FullName;

    [Fact]
    public async Task ReadsNativeManifestsAndKeepsModVersionSeparateFromGameCompatibility()
    {
        var token = TestContext.Current.CancellationToken;
        var user = Path.Combine(_root, "user");
        var local = Write("user/mods/local/manifest.txt", """
            Name "本地模组"
            Author "Test"
            Description "A description
            on another line"
            Version "v2.5"
            FileID "-1"
            """);
        Write("user/mods/legacy/manifest.txt", """Name "旧版订阅" Version "v1" FileID "456" """);
        Write("workshop/123/manifest.txt", """Name "工坊模组" Version "1.0" FileID "999" """);
        Write("workshop/124/manifest.txt", """Name "broken""");
        Write("user/mods/map/example.prison", "map");
        Write("user/mod/wrong/descriptor.mod", """name="不属于 PA 的描述文件" """);
        var scanner = new PrisonArchitectModScanner();

        var result = await scanner.ScanAsync(user, [Path.Combine(_root, "workshop")], token);

        Assert.Equal(3, result.Mods.Count);
        Assert.Single(result.Warnings);
        var localMod = Assert.Single(result.Mods, mod => mod.Name == "本地模组");
        Assert.Equal(Path.GetDirectoryName(local), localMod.ContentPath);
        Assert.Equal(local, localMod.DescriptorPath);
        Assert.Equal("v2.5", localMod.ModVersion);
        Assert.Null(localMod.SupportedGameVersion);
        Assert.Null(localMod.WorkshopId);
        Assert.Equal(ModOrigin.Local, localMod.Origin);
        Assert.Equal(123UL, Assert.Single(result.Mods, mod => mod.Name == "工坊模组").WorkshopId);
        Assert.Equal(456UL, Assert.Single(result.Mods, mod => mod.Name == "旧版订阅").WorkshopId);
    }

    [Fact]
    public async Task FirstManifestInTheModsDirectoryIsDetectedByTheNormalDiscoveryAndMonitor()
    {
        var token = TestContext.Current.CancellationToken;
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(user);
        var locator = new SteamGameLocator(GameCatalog.PrisonArchitect, [], _root);
        var monitor = new FileSystemLocalModMonitor(locator, new PrisonArchitectModScanner(), TimeSpan.FromMilliseconds(20));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var snapshots = monitor.WatchAsync(new(UserDataDirectory: user), deadline.Token).GetAsyncEnumerator();

        Assert.True(await snapshots.MoveNextAsync());
        Assert.Empty(snapshots.Current.Mods);
        var next = snapshots.MoveNextAsync().AsTask();
        Write("user/mods/first/manifest.txt", """Name "首次安装" Version "v1" """);

        Assert.True(await next);
        Assert.Equal("首次安装", Assert.Single(snapshots.Current.Mods).Name);
        Assert.Equal(Path.Combine(user, "mods"), Assert.Single(snapshots.Current.Environment!.LocalModDirectories));
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
