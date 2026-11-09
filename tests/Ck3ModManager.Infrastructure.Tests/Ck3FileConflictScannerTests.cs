using System.IO.Compression;
using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class Ck3FileConflictScannerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ck3-file-conflicts-");

    [Fact]
    public async Task ComparesSameSizeDirectoryAndZipFilesInConfigurationOrder()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, "first")).FullName;
        var zip = Path.Combine(_root.FullName, "third.zip");
        Write(directory, "gui/overlap.gui", "file A");
        Write(directory, "common/same.txt", "same");
        Write(directory, "graphics/case.txt", "case");
        Write(directory, "descriptor.mod", "test");
        Write(directory, ".metadata/info.txt", "test");
        Archive(zip, ("gui/overlap.gui", "file B"), ("common/same.txt", "same"), ("graphics/Case.txt", "case"),
            ("descriptor.mod", "metadata"), (".metadata/info.txt", "metadata"));

        var report = await new Ck3FileConflictScanner().CheckAsync([new(3, "ZIP", zip), new(1, "目录", directory)],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, report.CheckedMods);
        Assert.Empty(report.Warnings);
        Assert.Collection(report.Conflicts,
            file =>
            {
                Assert.Equal("common/same.txt", file.RelativePath);
                Assert.Equal(FileConflictKind.Identical, file.Kind);
            },
            file =>
            {
                Assert.Equal("gui/overlap.gui", file.RelativePath);
                Assert.Equal(FileConflictKind.Different, file.Kind);
                Assert.Equal(new[] { 1, 3 }, file.Mods.Select(mod => mod.Order));
            });
        Assert.False(File.Exists(Path.Combine(_root.FullName, "gui", "overlap.gui")));
    }

    [Fact]
    public async Task SkipsAmbiguousZipAndMissingInstallWithoutLosingOtherResults()
    {
        var first = Path.Combine(_root.FullName, "one");
        var second = Path.Combine(_root.FullName, "two");
        var unsafeZip = Path.Combine(_root.FullName, "unsafe.zip");
        var ambiguous = Path.Combine(_root.FullName, "duplicate.zip");
        Write(first, "common/same.txt", "first");
        Write(second, "common/same.txt", "second");
        Archive(unsafeZip, ("common/same.txt", "unsafe"), ("../escape.txt", "unsafe"));
        Archive(ambiguous, ("common/same.txt", "duplicate"), ("common/same.txt", "duplicate"));
        var report = await new Ck3FileConflictScanner().CheckAsync(
            [new(1, "正常 A", first), new(2, "重复 ZIP", ambiguous), new(3, "越界 ZIP", unsafeZip),
                new(4, "缺失 Mod", Path.Combine(_root.FullName, "missing")), new(5, "正常 B", second)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.CheckedMods);
        Assert.Equal(3, report.Warnings.Count);
        var collision = Assert.Single(report.Conflicts);
        Assert.Equal(new[] { 1, 5 }, collision.Mods.Select(mod => mod.Order));
        Assert.Equal(FileConflictKind.Different, collision.Kind);
        Assert.False(File.Exists(Path.Combine(_root.FullName, "escape.txt")));
    }

    [Fact]
    public async Task DoesNotFollowLinksOutOfAnInstallation()
    {
        if (OperatingSystem.IsWindows()) return; // 文件符号链接在部分 Windows 测试环境需要额外权限。
        var first = _root.CreateSubdirectory("normal").FullName;
        var second = _root.CreateSubdirectory("linked").FullName;
        var external = _root.CreateSubdirectory("external").FullName;
        Write(first, "asset.txt", "first");
        Write(external, "asset.txt", "external");
        File.CreateSymbolicLink(Path.Combine(second, "asset.txt"), Path.Combine(external, "asset.txt"));

        var report = await new Ck3FileConflictScanner().CheckAsync([new(1, "正常", first), new(2, "链接", second)],
            TestContext.Current.CancellationToken);
        Assert.Equal(1, report.CheckedMods);
        Assert.Empty(report.Conflicts);
        Assert.Contains("符号链接", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task IndexesDifferentTraitFilesAndExcludesOverwrittenFiles()
    {
        var one = _root.CreateSubdirectory("one").FullName;
        var two = _root.CreateSubdirectory("two").FullName;
        var three = Path.Combine(_root.FullName, "three.zip");
        Write(one, "common/traits/a.txt", "same_key = { } # comment\nonly_old = { }");
        Write(one, "common/traits/b.txt", "lost_key = { }");
        Write(one, "common/decisions/a.txt", "unscanned = { }");
        Write(two, "common/traits/b.txt", "other = { nested = { hidden = yes } }");
        Write(two, "common/traits/broken.txt", "malformed = {");
        Archive(three, ("common/traits/c.txt", "same_key = { name = \"x\" }\nlost_key = { }"),
            ("common/decisions/c.txt", "unscanned = { }"));

        var report = await new Ck3FileConflictScanner().CheckAsync([new(1, "一", one), new(2, "二", two), new(3, "三", three)],
            TestContext.Current.CancellationToken);
        Assert.Equal("common/traits/b.txt", Assert.Single(report.Conflicts).RelativePath);
        var definition = Assert.Single(report.DefinitionConflicts);
        Assert.Equal("same_key", definition.Key);
        Assert.Equal(new[] { "common/traits/a.txt", "common/traits/c.txt" }, definition.Sources.Select(source => source.RelativePath));
        Assert.Contains("broken.txt", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task DirectoryReplacementDropsPriorTraitDefinitionsAndReportsItsScope()
    {
        var first = _root.CreateSubdirectory("first").FullName;
        var replace = _root.CreateSubdirectory("replace").FullName;
        var third = _root.CreateSubdirectory("third").FullName;
        Write(first, "common/traits/a.txt", "old_key = { }");
        Write(replace, "common/traits/b.txt", "new_key = { }");
        Write(third, "common/traits/c.txt", "old_key = { }\nnew_key = { }");
        Write(replace, "descriptor.mod", "replace_path = \"common/traits\"");
        var report = await new Ck3FileConflictScanner().CheckAsync(
            [new(1, "原", first), new(2, "替换", replace) { DescriptorPath = Path.Combine(replace, "descriptor.mod") }, new(3, "后续", third)],
            TestContext.Current.CancellationToken);
        Assert.Equal("new_key", Assert.Single(report.DefinitionConflicts).Key);
        var replacement = Assert.Single(report.DirectoryReplacements);
        Assert.Equal("common/traits", replacement.RelativePath);
        Assert.Equal(1, replacement.EarlierPaths);
        Assert.Equal(2, replacement.Mod.Order);
    }

    [Fact]
    public async Task CancelledCheckDoesNotReadTheInstall()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Ck3FileConflictScanner().CheckAsync([new(1, "Mod", _root.FullName)], cancelled.Token));
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void Archive(string path, params (string Path, string Contents)[] files)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, contents) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(contents);
        }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
