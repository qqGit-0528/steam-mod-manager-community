using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop.Tests;

public sealed class PortableModTransferTests
{
    [AvaloniaFact]
    public async Task LocalImportPreviewRequiresConfirmationAndRefreshesTheLibrary()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var source = fixture.CreateSource();
        await vm.InitializeAsync(token);
        var window = new MainWindow { DataContext = vm, Width = 1220, Height = 960 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var open = window.FindControl<Button>("OpenLocalModImportButton")!;
            Assert.True(open.IsEffectivelyEnabled && open.IsEffectivelyVisible);
            await vm.PreviewLocalModImportAsync(source);
            Assert.True(vm.HasLocalModImportPreview);
            Assert.Empty(vm.InstalledMods);
            Assert.False(Directory.Exists(fixture.Environment.UserDataDirectory));

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var install = window.FindControl<Button>("ConfirmLocalModInstallButton")!;
            Assert.True(install.IsEffectivelyEnabled && install.IsEffectivelyVisible);
            Assert.Contains("离线测试 Mod", vm.LocalModImportSummary);
            SavePreview(window, "local-mod-import.png");
            vm.CancelLocalModInstallCommand.Execute(null);
            Assert.False(vm.HasLocalModImportPreview);
            Assert.Empty(vm.InstalledMods);

            await vm.PreviewLocalModImportAsync(source);
            await vm.ConfirmLocalModInstallAsync();
            Assert.False(vm.HasLocalModImportPreview);
            var mod = Assert.Single(vm.InstalledMods);
            Assert.Equal("离线测试 Mod", mod.Name);
            Assert.Equal(ModInstallManagement.Local, mod.Mod.Management);
            Assert.Same(mod, vm.SelectedInstalledMod);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PackagePreviewAndConfirmationCreateASeparateSavedConfiguration()
    {
        var token = TestContext.Current.CancellationToken;
        using var sender = new Fixture();
        using var receiver = new Fixture();
        using var outgoing = sender.Create();
        using var incoming = receiver.Create();
        await outgoing.InitializeAsync(token);
        await outgoing.PreviewLocalModImportAsync(sender.CreateSource());
        await outgoing.ConfirmLocalModInstallAsync();
        outgoing.Section = "profiles";
        outgoing.CreateProfile();
        outgoing.ProfileName = "配置测试";
        outgoing.SelectedInstalledMod = Assert.Single(outgoing.InstalledMods);
        outgoing.AddSelectedMod();
        Assert.True(outgoing.CanExportPlaysetPackage);
        var file = Path.Combine(sender.Root, "shared.ck3pack");
        await outgoing.ExportPlaysetPackageAsync(file);

        await incoming.InitializeAsync(token);
        incoming.Section = "profiles";
        var window = new MainWindow { DataContext = incoming, Width = 1220, Height = 960 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var import = window.FindControl<Button>("ImportPlaysetPackageButton")!;
            Assert.True(import.IsEffectivelyVisible && import.IsEffectivelyEnabled);
            await incoming.PreviewPlaysetPackageAsync(file);
            Assert.True(incoming.HasProfileTransferPreview);
            Assert.Equal("离线测试 Mod", Assert.Single(incoming.PlaysetPackageRows).Name);
            Assert.Empty(incoming.Profiles);
            Assert.False(Directory.Exists(receiver.Environment.UserDataDirectory));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var confirm = window.FindControl<Button>("ConfirmPlaysetPackageImportButton")!;
            Assert.True(confirm.IsEffectivelyEnabled && confirm.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "快照包预览");
            SavePreview(window, "playset-package-preview.png");

            await incoming.ConfirmPlaysetPackageImportAsync();
            Assert.False(incoming.HasProfileTransferPreview);
            var restored = Assert.Single(incoming.Profiles);
            Assert.NotEqual(outgoing.SelectedProfile!.Id, restored.Id);
            Assert.StartsWith("配置测试", restored.Name);
            Assert.Equal(restored, incoming.SelectedProfile);
            Assert.Equal(restored.Id, Assert.Single(await receiver.Profiles.LoadAsync(token)).Id);
            Assert.True(Assert.Single(incoming.ProfileMods).Enabled);
            Assert.Equal(ModInstallManagement.Local, Assert.Single(incoming.InstalledMods).Mod.Management);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PackageProfileSaveFailureRollsBackOnlyTheNewCopyAndCanRetry()
    {
        var token = TestContext.Current.CancellationToken;
        using var sender = new Fixture();
        using var receiver = new Fixture();
        var source = sender.CreateSource();
        var install = await sender.Workspace.PreviewLocalModImportAsync(new(), source, token);
        await sender.Workspace.InstallLocalModAsync(new(), install, token);
        var mod = Assert.Single((await sender.Workspace.ScanLocalModsAsync(new(), token)).Mods);
        var profile = new ModProfile(Guid.NewGuid(), "来源配置", null,
            [new(mod.Id, mod.Name, mod.ContentPath, null, mod.SupportedGameVersion)], DateTimeOffset.UtcNow);
        var file = Path.Combine(sender.Root, "shared.ck3pack");
        await sender.Workspace.ExportPlaysetPackageAsync(new(), profile, file, token);

        var preview = await receiver.Workspace.PreviewPlaysetPackageAsync(file, token);
        var original = Directory.CreateDirectory(Path.Combine(receiver.Environment.UserDataDirectory, "mod")).FullName;
        File.WriteAllText(Path.Combine(original, "keep.txt"), "untouched");
        receiver.Profiles.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => receiver.Workspace.ImportPlaysetPackageAsync(new(), preview, token));
        Assert.Equal("keep.txt", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(original))));
        Assert.Empty(await receiver.Profiles.LoadAsync(token));

        receiver.Profiles.FailSave = false;
        var restored = await receiver.Workspace.ImportPlaysetPackageAsync(new(), preview, token);
        Assert.Equal(restored.Id, Assert.Single(await receiver.Profiles.LoadAsync(token)).Id);
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(original, "keep.txt")));
    }

    private static void SavePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CK3_BATCH3_SCREENSHOTS") is { Length: > 0 } directory)
            window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class Fixture : IGameLocator, IWorkshopSource, IExternalNavigator, IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ck3-transfer-ui-").FullName;
        public GameEnvironment Environment => new(Path.Combine(Root, "game"), Path.Combine(Root, "user"), "1.20.1", [], []);
        public ProfileStore Profiles { get; } = new();
        public WorkspaceService Workspace => new(this, new Ck3ModScanner(), Profiles, new JsonSettingsStore(Root),
            playsetSnapshots: new Ck3PlaysetSnapshotStore(Root), localModInstaller: new Ck3LocalModInstaller());

        public MainWindowViewModel Create() => new(Workspace, new WorkshopService(this), this);

        public string CreateSource()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, "source")).FullName;
            File.WriteAllText(Path.Combine(folder, "descriptor.mod"),
                "name=\"离线测试 Mod\" version=\"1\" supported_version=\"1.20.*\"");
            File.WriteAllText(Path.Combine(folder, "asset.txt"), "old asset");
            return folder;
        }

        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Environment);

        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("本地导入不应访问工坊。");

        public void LaunchGame() { }
        public void OpenWorkshop(ulong? publishedFileId = null) { }
        public void OpenDirectory(string path) { }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ProfileStore : IProfileStore
    {
        public bool FailSave { get; set; }
        private readonly List<ModProfile> _items = [];
        public Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModProfile>>(_items.ToArray());
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("测试保存失败");
            _items.Add(profile);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(profile => profile.Id == id);
            return Task.CompletedTask;
        }
    }
}
