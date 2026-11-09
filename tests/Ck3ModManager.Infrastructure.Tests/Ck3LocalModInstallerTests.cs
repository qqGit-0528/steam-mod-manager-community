using System.IO.Compression;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class Ck3LocalModInstallerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ck3-local-import-");
    private GameEnvironment Environment => new(null, Path.Combine(_root.FullName, "user"), null, [], []);
    private const string Descriptor = """
        name="测试 Mod"
        version="1.2"
        supported_version="1.20.*"
        path="/outside/content"
        remote_file_id="42"
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewsAndInstallsOneModWithALocalWrapper(bool zip)
    {
        var token = TestContext.Current.CancellationToken;
        var source = zip ? WriteZip(("Packed/descriptor.mod", Descriptor), ("Packed/common/asset.txt", "original"), ("README.txt", "package"))
            : WriteFolder();
        var installer = new Ck3LocalModInstaller();
        var review = await installer.PreviewAsync(Environment, source, token);
        Assert.Equal(("测试 Mod", "1.2", 2), (review.Name, review.ModVersion, review.FileCount));
        Assert.False(Directory.Exists(Environment.UserDataDirectory));
        var installed = await installer.InstallAsync(Environment, review, token);

        Assert.Equal(review.TargetPath, installed);
        Assert.Equal("original", File.ReadAllText(Path.Combine(installed, "common", "asset.txt")));
        Assert.DoesNotContain("/outside/", File.ReadAllText(installed + ".mod"));
        Assert.False(File.Exists(Path.Combine(installed, "README.txt")));
        var scan = await new Ck3ModScanner().ScanAsync(Environment, token);
        Assert.Empty(scan.Warnings);
        var mod = Assert.Single(scan.Mods);
        Assert.Equal(ModInstallManagement.Local, mod.Management);
        Assert.Equal(42UL, mod.WorkshopId);
        Assert.Equal("1.2", mod.ModVersion);
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(Environment, review, token));
        Assert.Equal("original", File.ReadAllText(Path.Combine(installed, "common", "asset.txt")));

        var remover = new InstalledModRemover(Ck3.AppId, new FileWorkshopDownloadStore(_root.FullName),
            (_, _, _) => throw new InvalidOperationException("本地安装不应触发退订"));
        await remover.RemoveAsync(Environment, mod, token);
        Assert.Empty((await new Ck3ModScanner().ScanAsync(Environment, token)).Mods);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(@"C:\escape.txt")]
    [InlineData("Common/ASSET.txt")]
    [InlineData("common/asset.txt/child.txt")]
    [InlineData("common/NUL.txt")]
    public async Task RejectsUnsafeZipPathsBeforeWriting(string unsafeEntry)
    {
        var source = WriteZip(("descriptor.mod", Descriptor), ("common/asset.txt", "original"), (unsafeEntry, "outside"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new Ck3LocalModInstaller().PreviewAsync(Environment, source, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Environment.UserDataDirectory));
    }

    [Fact]
    public async Task RejectsZipLinksAndAmbiguousModRoots()
    {
        var token = TestContext.Current.CancellationToken;
        var installer = new Ck3LocalModInstaller();
        var source = WriteZip(("descriptor.mod", Descriptor), ("common/asset.txt", "original"));
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            archive.CreateEntry("linked").ExternalAttributes = unchecked((int)0xa0000000);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PreviewAsync(Environment, source, token));

        var ambiguous = WriteZip(("one/descriptor.mod", Descriptor), ("two/descriptor.mod", Descriptor));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PreviewAsync(Environment, ambiguous, token));
        Assert.False(Directory.Exists(Environment.UserDataDirectory));
    }

    [Fact]
    public async Task ChangedSourceAndCancellationLeaveNoPublishedInstallAndAllowRetry()
    {
        var token = TestContext.Current.CancellationToken;
        var installer = new Ck3LocalModInstaller();
        var source = WriteFolder();
        var review = await installer.PreviewAsync(Environment, source, token);
        File.AppendAllText(Path.Combine(source, "common", "asset.txt"), " changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Environment, review, token));
        Assert.False(Directory.Exists(Environment.UserDataDirectory));

        var updated = await installer.PreviewAsync(Environment, source, token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Environment, updated, cancelled.Token));
        Assert.False(Directory.Exists(Environment.UserDataDirectory));
        Assert.Equal(updated.TargetPath, await installer.InstallAsync(Environment, updated, token));
    }

    [Fact]
    public async Task ConcurrentConfirmationPublishesOnlyOneInstall()
    {
        var token = TestContext.Current.CancellationToken;
        var source = WriteFolder();
        var installer = new Ck3LocalModInstaller();
        var review = await installer.PreviewAsync(Environment, source, token);

        async Task<bool> InstallAsync()
        {
            try { await installer.InstallAsync(Environment, review, token); return true; }
            catch (IOException) { return false; }
        }

        var outcomes = await Task.WhenAll(InstallAsync(), InstallAsync());
        Assert.Single(outcomes, completed => completed);
        Assert.Single((await new Ck3ModScanner().ScanAsync(Environment, token)).Mods);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(review.TargetPath)!, ".stage-*"));
    }

    private string WriteFolder()
    {
        var directory = _root.CreateSubdirectory("source").FullName;
        File.WriteAllText(Path.Combine(directory, "descriptor.mod"), Descriptor);
        Directory.CreateDirectory(Path.Combine(directory, "common"));
        File.WriteAllText(Path.Combine(directory, "common", "asset.txt"), "original");
        return directory;
    }

    private string WriteZip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = new StreamWriter(zip.CreateEntry(entry.Path).Open());
            stream.Write(entry.Content);
        }
        return path;
    }

    public void Dispose() => _root.Delete(recursive: true);
}
