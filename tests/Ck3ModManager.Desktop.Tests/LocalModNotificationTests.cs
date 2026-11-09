using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class LocalModNotificationTests
{
    [AvaloniaFact]
    public async Task FileEventsUpdateLocalLibraryAutomaticallyWithoutRefreshButton()
    {
        var installed = Mod("installed");
        var ports = new GamePorts([installed]); var monitor = new ControlledMonitor();
        using var vm = Create(ports, monitor);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var session = await monitor.NextSessionAsync();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Null(window.FindControl<Button>("RefreshButton"));
            var added = Mod("new");
            await session.PublishAsync(installed, added);
            Assert.Equal(new[] { "installed", "new" }, vm.InstalledMods.Select(row => row.Mod.Id));
            await session.PublishAsync(added);
            Assert.Equal("new", Assert.Single(vm.InstalledMods).Mod.Id);
            await session.PublishAsync();
            Assert.Empty(vm.InstalledMods);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task SamePathUpdatesVersionAndCompatibilityWhileIdenticalScansKeepTheRow()
    {
        var installed = Mod("installed");
        var ports = new GamePorts([installed]); var monitor = new ControlledMonitor();
        using var vm = Create(ports, monitor);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var session = await monitor.NextSessionAsync();
        var original = Assert.Single(vm.InstalledMods);
        vm.SelectedInstalledMod = original;

        await session.PublishAsync(installed with { Dependencies = new[] { "dependency" } });
        var withDependency = Assert.Single(vm.InstalledMods);
        await session.PublishAsync(installed with { Dependencies = new[] { "dependency" } });
        Assert.Same(withDependency, vm.SelectedInstalledMod);
        Assert.Same(withDependency, Assert.Single(vm.InstalledMods));

        await session.PublishAsync(installed with { Name = "新版本", ModVersion = "2.0", SupportedGameVersion = "1.19" });
        var updated = Assert.Single(vm.InstalledMods);
        Assert.Same(updated, vm.SelectedInstalledMod);
        Assert.Equal(original.Mod.Id, updated.Mod.Id);
        Assert.Equal("新版本", updated.Name);
        Assert.Equal("2.0", updated.Mod.ModVersion);
        Assert.True(updated.IsCompatibilityMismatch);
    }

    [Fact]
    public async Task PartialSnapshotsAndScanFailuresPreserveKnownModsAndProfileDraft()
    {
        var installed = Mod("installed"); var added = Mod("new");
        var ports = new GamePorts([installed]); var monitor = new ControlledMonitor();
        using var vm = Create(ports, monitor);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var session = await monitor.NextSessionAsync();
        vm.CreateProfile(); vm.ProfileName = "未保存战役";
        vm.SelectedInstalledMod = vm.InstalledMods[0]; vm.AddSelectedMod();
        await session.PublishAsync(installed, added);
        Assert.Equal(2, vm.InstalledMods.Count);
        Assert.Equal("未保存战役", vm.ProfileName);
        Assert.Single(vm.ProfileMods);
        await session.PublishWithWarningsAsync(installed);
        Assert.Equal(2, vm.InstalledMods.Count);
        ports.ScanFailure = new IOException("扫描暂时失败");
        await vm.RefreshAsync();
        Assert.True(vm.HasError);
        Assert.Equal(2, vm.InstalledMods.Count);
        Assert.Single(vm.ProfileMods);
        await session.FailAsync(new IOException("检测目录暂时不可读"));
        Assert.Equal(2, vm.InstalledMods.Count);
    }

    [Fact]
    public async Task SwitchingGamesIgnoresLateFileEventsAfterSwitchOrDispose()
    {
        var monitors = new Dictionary<string, ControlledMonitor>();
        using var manager = new GameWorkspaceManager(game =>
        {
            var monitor = new ControlledMonitor(ignoreCancellation: true); monitors.Add(game.Id, monitor);
            return Create(new GamePorts([Mod(game.Id)]), monitor, game);
        });
        var token = TestContext.Current.CancellationToken;
        await manager.InitializeAsync(token); var ck3 = manager.Current;
        var oldSession = await monitors["ck3"].NextSessionAsync();
        await oldSession.PublishAsync(Mod("ck3"), Mod("new"));
        Assert.Equal(2, ck3.InstalledMods.Count);
        await manager.SelectGameAsync(GameCatalog.Hoi4, token); var hoi4 = manager.Current;
        var hoi4Session = await monitors["hoi4"].NextSessionAsync();
        await oldSession.PublishAsync(Mod("late"));
        Assert.Equal(2, ck3.InstalledMods.Count);
        Assert.Equal("hoi4", Assert.Single(hoi4.InstalledMods).Mod.Id);
        await manager.SelectGameAsync(GameCatalog.Ck3, token);
        var resumed = await monitors["ck3"].NextSessionAsync();
        Assert.True(hoi4Session.Cancellation.IsCancellationRequested);
        await resumed.PublishAsync(Mod("resumed"));
        Assert.Equal("resumed", Assert.Single(ck3.InstalledMods).Mod.Id);
        manager.Dispose();
        await resumed.PublishAsync(Mod("late-after-dispose"));
        Assert.Equal("resumed", Assert.Single(ck3.InstalledMods).Mod.Id);
    }

    private static InstalledMod Mod(string id, bool available = true) => new(id, id, $"test/{id}", $"test/{id}.mod",
        "1.0", "1.20", null, [], ModOrigin.Local, available);

    private static MainWindowViewModel Create(GamePorts ports, ControlledMonitor monitor, GameDefinition? game = null) =>
        new(new WorkspaceService(ports, ports, ports, ports), new WorkshopService(ports, game: game), ports,
            localModMonitor: monitor);

    private sealed class GamePorts(IReadOnlyList<InstalledMod> mods) : IGameLocator, IModScanner,
        IProfileStore, ISettingsStore, IWorkshopSource, IExternalNavigator
    {
        public IReadOnlyList<InstalledMod> Mods { get; set; } = mods;
        public Exception? ScanFailure { get; set; }
        private AppSettings _settings = new();

        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameEnvironment(null, options.UserDataDirectory ?? "test-user-data", "1.20.1", [], []));
        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
            CancellationToken cancellationToken = default) => ScanFailure is { } error
                ? Task.FromException<ModScanResult>(error)
                : Task.FromResult(new ModScanResult(Mods.ToArray(), []));
        public Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        { _settings = settings; return Task.CompletedTask; }
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("本地更新提示测试不应查询工坊。");
        public void LaunchGame() => throw new InvalidOperationException("本地更新提示测试不应启动游戏。");
        public void OpenWorkshop(ulong? publishedFileId = null) => throw new InvalidOperationException("本地更新提示测试不应打开工坊。");
        public void OpenDirectory(string path) => throw new InvalidOperationException("本地更新提示测试不应打开目录。");
    }

    private sealed class ControlledMonitor(bool ignoreCancellation = false) : ILocalModMonitor
    {
        private readonly Channel<WatchSession> _sessions = Channel.CreateUnbounded<WatchSession>();

        public async Task<WatchSession> NextSessionAsync() => await _sessions.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public async IAsyncEnumerable<ModScanResult> WatchAsync(DiscoveryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var session = new WatchSession(options, cancellationToken);
            _sessions.Writer.TryWrite(session);
            Delivery? pending = null;
            try
            {
                while (true)
                {
                    pending = await session.Deliveries.Reader.ReadAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                    if (pending.Error is { } error) throw error;
                    yield return pending.Scan!;
                    pending.Consumed.TrySetResult();
                    pending = null;
                }
            }
            finally { pending?.Consumed.TrySetResult(); }
        }
    }

    private sealed class WatchSession(DiscoveryOptions options, CancellationToken cancellation)
    {
        public DiscoveryOptions Options { get; } = options;
        public CancellationToken Cancellation { get; } = cancellation;
        public Channel<Delivery> Deliveries { get; } = Channel.CreateUnbounded<Delivery>();
        public Task PublishAsync(params InstalledMod[] mods) => SendAsync(new Delivery(new ModScanResult(mods, [])));
        public Task PublishWithWarningsAsync(params InstalledMod[] mods) => SendAsync(new Delivery(new ModScanResult(mods, ["部分目录暂时不可读"])));
        public Task FailAsync(Exception error) => SendAsync(new Delivery(null, error));

        private async Task SendAsync(Delivery delivery)
        {
            await Deliveries.Writer.WriteAsync(delivery, TestContext.Current.CancellationToken);
            await delivery.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class Delivery(ModScanResult? scan, Exception? error = null)
    {
        public ModScanResult? Scan { get; } = scan;
        public Exception? Error { get; } = error;
        public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
