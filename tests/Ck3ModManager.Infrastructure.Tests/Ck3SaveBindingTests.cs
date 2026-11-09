using System.IO.Compression;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class Ck3SaveBindingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ck3-save-binding-").FullName;
    private const string Header = """
        meta_data={
            version "1.20.0.4"
            title "quoted brace } \" still in text"
            # close brace in comment }
            mods { "mod/ugc_12.mod" "mod/custom.mod" }
        }
        """;

    [Fact]
    public async Task ReadsSAVHeaderWithoutParsingTheGameState()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "plain.ck3");
        File.WriteAllText(path, "SAV01006ebce06a00005b68\n" + Header + "\nunterminated_body={");
        var save = await new Ck3SaveBindingStore(_root).ReadAsync(path, token);
        Assert.Equal("1.20.0.4", save.Metadata.GameVersion);
        Assert.Equal(new[] { "mod/ugc_12.mod", "mod/custom.mod" }, save.Metadata.ModDescriptors);
        Assert.Null(save.Binding);
    }

    [Theory]
    [InlineData("meta", "version=\"1.20.0.4\"\nmods={}")]
    [InlineData("gamestate", Header)]
    public async Task ReadsMetadataInsideACompressedSave(string entry, string text)
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "compressed.ck3");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var output = new StreamWriter(archive.CreateEntry(entry).Open()))
            output.Write(text);
        var save = await new Ck3SaveBindingStore(_root).ReadAsync(path, token);
        Assert.Equal("1.20.0.4", save.Metadata.GameVersion);
        Assert.Equal(entry == "meta" ? 0 : 2, save.Metadata.ModDescriptors!.Count);
    }

    [Fact]
    public async Task CorruptMetadataCanBeBoundAndAnOverwrittenOrMovedSaveRequiresConfirmation()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "autosave.ck3");
        File.WriteAllText(path, "SAV\nmeta_data={ damaged");
        var store = new Ck3SaveBindingStore(_root);
        var unknown = await store.ReadAsync(path, token);
        Assert.Equal(new Ck3SaveMetadata(null, null), unknown.Metadata);
        var snapshot = Guid.NewGuid();
        var bound = await store.BindAsync(unknown, snapshot, token);
        var persisted = await new Ck3SaveBindingStore(_root).ReadAsync(path, token);
        Assert.Equal(bound.Binding, persisted.Binding);
        Assert.False(persisted.NeedsRebind);
        File.WriteAllText(path, Header);
        var overwritten = await store.ReadAsync(path, token);
        Assert.True(overwritten.NeedsRebind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BindAsync(bound, snapshot, token));
        Assert.False((await store.BindAsync(overwritten, snapshot, token)).NeedsRebind);
        var movedPath = Path.Combine(_root, "renamed.ck3");
        File.Move(path, movedPath);
        Assert.True((await store.ReadAsync(path, token)).NeedsRebind);
        Assert.Null((await store.ReadAsync(movedPath, token)).Binding);
    }

    [Fact]
    public async Task MetadataBeyondTheReadLimitRemainsUnknown()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "large.ck3");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var output = new StreamWriter(archive.CreateEntry("gamestate").Open()))
            output.Write("meta_data={ " + new string(' ', 1024 * 1024) + "version=1.20.0.4 }");
        var save = await new Ck3SaveBindingStore(_root).ReadAsync(path, token);
        Assert.Equal(new Ck3SaveMetadata(null, null), save.Metadata);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
