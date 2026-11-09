using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class MultiGameWorkspaceTests
{
    [Fact]
    public async Task SwitchingGamesPreservesDraftsPathsAndVersionOverridesSeparately()
    {
        var fixture = new Fixture();
        using var manager = fixture.CreateManager();
        var token = TestContext.Current.CancellationToken;
        await manager.InitializeAsync(token);
        var ck3 = manager.Current;
        ck3.CreateProfile();
        ck3.ProfileName = "未保存的 CK3 战役";
        ck3.SelectedInstalledMod = Assert.Single(ck3.InstalledMods);
        ck3.AddSelectedMod();
        ck3.LocalSearch = "CK3";
        ck3.GamePath = "unsaved-ck3-path";
        ck3.WorkshopTargetVersion = "1.19.2";

        await manager.SelectGameAsync(GameCatalog.Hoi4, token);
        var hoi4 = manager.Current;
        Assert.Empty(hoi4.Profiles);
        Assert.Empty(hoi4.ProfileMods);
        Assert.Empty(hoi4.LocalSearch);
        Assert.Empty(hoi4.GamePath);
        Assert.Equal("1.17.1", hoi4.WorkshopTargetVersion);
        Assert.Equal("hoi4-mod", Assert.Single(hoi4.InstalledMods).Mod.Id);
        hoi4.CreateProfile();
        hoi4.ProfileName = "钢铁雄心战役";
        hoi4.GamePath = "unsaved-hoi4-path";
        hoi4.LaunchGameCommand.Execute(null);
        Assert.Equal(1, fixture.States["hoi4"].LaunchRequests);
        Assert.Equal(0, fixture.States["ck3"].LaunchRequests);
        Assert.Contains("HOI4", hoi4.Status);

        await manager.SelectGameAsync(GameCatalog.Ck3, token);
        Assert.Same(ck3, manager.Current);
        Assert.Equal("未保存的 CK3 战役", ck3.ProfileName);
        Assert.Equal("ck3-mod", Assert.Single(ck3.ProfileMods).Mod.InstalledModId);
        Assert.True(ck3.IsProfileDirty);
        Assert.Equal("CK3", ck3.LocalSearch);
        Assert.Equal("unsaved-ck3-path", ck3.GamePath);
        Assert.Equal("1.19.2", ck3.WorkshopTargetVersion);

        await manager.SelectGameAsync(GameCatalog.Hoi4, token);
        Assert.Same(hoi4, manager.Current);
        Assert.Equal("钢铁雄心战役", hoi4.ProfileName);
        Assert.Equal("unsaved-hoi4-path", hoi4.GamePath);
        Assert.All(fixture.States.Values, state => Assert.Equal(1, state.DiscoveryCalls));
    }

    [Fact]
    public async Task PreviousGamesLateInitializationCannotReplaceCurrentWorkspace()
    {
        var fixture = new Fixture { DelayCk3Discovery = true };
        using var manager = fixture.CreateManager();
        var token = TestContext.Current.CancellationToken;
        var original = manager.Current;
        var initialization = manager.InitializeAsync(token);
        try
        {
            Assert.True(original.IsBusy);
            await manager.SelectGameAsync(GameCatalog.Hoi4, token);
            Assert.Same(GameCatalog.Hoi4, manager.Current.Game);
            Assert.Equal("hoi4-mod", Assert.Single(manager.Current.InstalledMods).Mod.Id);
        }
        finally { fixture.States["ck3"].DiscoveryReady.TrySetResult(); }

        await initialization;
        Assert.Same(GameCatalog.Hoi4, manager.Current.Game);
        Assert.Equal("hoi4-mod", Assert.Single(manager.Current.InstalledMods).Mod.Id);
        Assert.Equal("ck3-mod", Assert.Single(original.InstalledMods).Mod.Id);
    }

    [Fact]
    public async Task PreviousGamesLateWorkshopDetailStaysInItsOwnSession()
    {
        var fixture = new Fixture();
        using var manager = fixture.CreateManager();
        var token = TestContext.Current.CancellationToken;
        await manager.InitializeAsync(token);
        var original = manager.Current;
        var source = fixture.States["ck3"];
        source.DelayLookup = true;
        original.WorkshopInput = "123";
        var lookup = original.LookupWorkshopAsync();
        try
        {
            Assert.True(original.WorkshopWorking);
            await manager.SelectGameAsync(GameCatalog.Hoi4, token);
            Assert.False(manager.Current.HasWorkshopItem);
        }
        finally { source.LookupReady.TrySetResult(); }

        await lookup;
        Assert.Same(GameCatalog.Hoi4, manager.Current.Game);
        Assert.False(manager.Current.HasWorkshopItem);
        await manager.SelectGameAsync(GameCatalog.Ck3, token);
        Assert.Same(original, manager.Current);
        Assert.Equal(Ck3.AppId, original.WorkshopItem!.ConsumerAppId);
    }

    [AvaloniaFact]
    public async Task GameSelectorSwitchesWindowContextAndLaunchBinding()
    {
        var fixture = new Fixture();
        using var manager = fixture.CreateManager();
        var token = TestContext.Current.CancellationToken;
        await manager.InitializeAsync(token);
        var original = manager.Current;
        var window = new MainWindow { DataContext = original };
        var application = Avalonia.Application.Current!;
        var previousTheme = new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush", "AccentSoftHoverBrush", "AccentLineBrush" }
            .ToDictionary(key => key, key => application.Resources[key]);
        GameTheme.Apply(original.Game);
        manager.WorkspaceChanged += workspace => { window.DataContext = workspace; GameTheme.Apply(workspace.Game); };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var selector = window.FindControl<ComboBox>("GameSelector")!;
            Assert.Equal(GameCatalog.All.Count, selector.Items.Count);
            Assert.Same(GameCatalog.Ck3, selector.SelectedItem);

            selector.SelectedItem = GameCatalog.Hoi4;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(GameCatalog.Hoi4, manager.Current.Game);
            await manager.InitializeAsync(token);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(manager.Current, window.DataContext);
            Assert.Same(GameCatalog.Hoi4, selector.SelectedItem);
            Assert.Contains(GameCatalog.Hoi4.DisplayName, window.Title);
            Assert.Same(manager.Current.LaunchGameCommand, window.FindControl<Button>("LaunchGameButton")!.Command);

            var screenshots = Environment.GetEnvironmentVariable("PARADOX_UI_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(screenshots))
            {
                Directory.CreateDirectory(screenshots);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "paradox-overview-preview.png"), PngBitmapEncoderOptions.Default);
                window.Width = 1000;
                window.Height = 690;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "paradox-minimum-preview.png"), PngBitmapEncoderOptions.Default);
                manager.Current.Section = "workshop";
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "paradox-workshop-minimum-preview.png"), PngBitmapEncoderOptions.Default);
                manager.Current.Section = "settings";
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "paradox-settings-minimum-preview.png"), PngBitmapEncoderOptions.Default);
            }

            selector.SelectedItem = GameCatalog.Ck3;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(GameCatalog.Ck3, manager.Current.Game);
            await manager.InitializeAsync(token);
            Assert.Same(original, window.DataContext);
            Assert.Same(GameCatalog.Ck3, selector.SelectedItem);

            foreach (var game in GameCatalog.All)
            {
                selector.SelectedItem = game;
                Dispatcher.UIThread.RunJobs();
                Assert.Same(game, manager.Current.Game);
                await manager.InitializeAsync(token);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(game.DisplayName, window.Title);
                Assert.Same(manager.Current.LaunchGameCommand, window.FindControl<Button>("LaunchGameButton")!.Command);
                var expected = new SteamGameIconConverter().Convert(game, typeof(Bitmap), null, CultureInfo.InvariantCulture);
                Assert.Same(expected, window.FindControl<Image>("HeaderGameIcon")!.Source);
                Assert.Same(expected, window.FindControl<Image>("CurrentGameIcon")!.Source);
                Assert.Contains(selector.GetVisualDescendants().OfType<Image>(), image => ReferenceEquals(expected, image.Source));
                var accent = Assert.IsType<SolidColorBrush>(application.Resources["AccentBrush"]);
                Assert.Equal(Color.Parse(game.AccentColor), accent.Color);
                Assert.Same(accent, window.FindControl<Button>("LaunchGameButton")!.Background);
                if (!string.IsNullOrWhiteSpace(screenshots))
                {
                    manager.Current.Section = "overview";
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, $"steam-icons-{game.Id}.png"), PngBitmapEncoderOptions.Default);
                    if (game == GameCatalog.Imperator)
                    {
                        selector.IsDropDownOpen = true;
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "steam-icons-dropdown.png"), PngBitmapEncoderOptions.Default);
                        selector.IsDropDownOpen = false;
                    }
                }
            }
        }
        finally
        {
            window.Close();
            foreach (var (key, value) in previousTheme) application.Resources[key] = value;
        }
    }

    [AvaloniaFact]
    public async Task DiscoveredGamesAppearInSelectorAndUseTheirOwnWorkspace()
    {
        var token = TestContext.Current.CancellationToken;
        var newGame = new GameDefinition("steam-294100", 294100, "RimWorld", "RimWorld", "RimWorld", "RimWorld")
        {
            IsDiscovered = true
        };
        var secondGame = new GameDefinition("steam-123456", 123456, "Another Game", "Another Game", "Another Game", "AnotherGame")
        {
            IsDiscovered = true,
            HasSteamWorkshop = false
        };
        IReadOnlyList<GameDefinition> discovered = [.. GameCatalog.All, newGame];
        var fixture = new Fixture();
        using var manager = fixture.CreateManager((_, _) => Task.FromResult(discovered));
        var window = new MainWindow { DataContext = manager.Current };
        manager.WorkspaceChanged += vm => window.DataContext = vm;
        try
        {
            window.Show();
            await manager.InitializeAsync(token);
            var selector = window.FindControl<ComboBox>("GameSelector")!;
            Assert.Contains(newGame, selector.Items);
            Assert.Same(GameCatalog.Ck3, selector.SelectedItem);

            selector.SelectedItem = newGame;
            await manager.SelectGameAsync(newGame, token);
            Assert.Same(manager.Current, window.DataContext);
            Assert.Equal(newGame, manager.Current.Game);
            Assert.True(manager.Current.CanOpenDiscoveredWorkshop);
            Assert.False(manager.Current.SupportsGameActivation);
            Assert.Equal("steam-294100-mod", Assert.Single(manager.Current.InstalledMods).Mod.Id);

            discovered = [.. GameCatalog.All, newGame, secondGame];
            selector.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(secondGame, selector.Items);
            Assert.Same(newGame, selector.SelectedItem);
        }
        finally { window.Close(); }
    }

    private sealed class Fixture
    {
        public Dictionary<string, GameState> States { get; } = [];
        public bool DelayCk3Discovery { get; init; }
        public GameWorkspaceManager CreateManager(
            Func<string?, CancellationToken, Task<IReadOnlyList<GameDefinition>>>? discoverGames = null) => new(game =>
        {
            var state = new GameState(game) { DelayDiscovery = DelayCk3Discovery && game.Id == "ck3" };
            States.Add(game.Id, state);
            return new MainWindowViewModel(new WorkspaceService(state, state, state, state),
                new WorkshopService(state, game: game), state);
        }, discoverGames: discoverGames);
    }

    private sealed class GameState(GameDefinition game) : IGameLocator, IModScanner, IProfileStore,
        ISettingsStore, IWorkshopSource, IExternalNavigator
    {
        public bool DelayDiscovery { get; init; }
        public bool DelayLookup { get; set; }
        public int DiscoveryCalls { get; private set; }
        public int LaunchRequests { get; private set; }
        public TaskCompletionSource DiscoveryReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LookupReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string Version => game.Id == "ck3" ? "1.20.1" : "1.17.1";

        public async Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default)
        {
            DiscoveryCalls++;
            if (DelayDiscovery) await DiscoveryReady.Task.WaitAsync(cancellationToken);
            return new GameEnvironment($"test/{game.Id}/game", $"test/{game.Id}/userdata", Version, [], []);
        }

        public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
            CancellationToken cancellationToken = default) => Task.FromResult(new ModScanResult([
                new InstalledMod($"{game.Id}-mod", $"{game.ShortName} 测试 Mod", $"test/{game.Id}/mod", "test.mod",
                    "1.0", Version, null, [], ModOrigin.Local, true)
            ], []));

        public async Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            if (DelayLookup) await LookupReady.Task.WaitAsync(cancellationToken);
            return new WorkshopItem(publishedFileId, game.AppId, $"{game.ShortName} 工坊 Mod", "测试说明", null,
                new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}"), DateTimeOffset.UtcNow,
                1, [Version], null);
        }

        public Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModProfile>>([]);
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LaunchGame() => LaunchRequests++;
        public void OpenWorkshop(ulong? publishedFileId = null) { }
        public void OpenDirectory(string path) { }
    }
}
