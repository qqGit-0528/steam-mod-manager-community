using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        UiLocale.ApplyResources(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ck3ModManager");
            var language = new JsonUiLanguagePreference(root);
            try { UiLocale.UseLanguage(language.LoadAsync().GetAwaiter().GetResult()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { UiLocale.UseLanguage(AppText.Chinese); }
            var http = SteamHttpClients.CreateMetadataClient();
            var imageTransport = new SteamWorkshopImageSource();
            var images = new CachedWorkshopImageSource(imageTransport, root);
            var downloadServices = new List<WorkshopDownloadService>();
            var gameDiscovery = new SteamGameDiscovery(http, settingsDirectory: root);
            var downloadStore = new FileWorkshopDownloadStore(root);
            var installedContent = new FileInstalledModContent();
            var workshopDependencies = new SteamWorkshopDependencySource(http);
            var workspaces = new GameWorkspaceManager(game =>
            {
                var gameRoot = GameStorage.GetDirectory(root, game);
                var locator = new SteamGameLocator(game);
                IModScanner scanner = game.AppId == GameCatalog.PrisonArchitect.AppId
                    ? new PrisonArchitectModScanner(downloadStore) : new Ck3ModScanner(game.IsDiscovered, downloadStore, game.AppId);
                var settings = new JsonSettingsStore(gameRoot);
                var nativeSteam = game.IsDiscovered ? null : new SteamClientWorkshopDownloader(game, locator, settings, root);
                var workspace = new WorkspaceService(locator, scanner,
                    new JsonProfileStore(gameRoot), settings,
                    game.IsDiscovered || game.Id == "prisonarchitect" ? null : new ParadoxGameModActivator(game, gameRoot),
                    game.AppId == GameCatalog.Ck3.AppId ? new Ck3PlaysetSnapshotStore(gameRoot) : null,
                    game.AppId == GameCatalog.Ck3.AppId ? new Ck3SaveBindingStore(gameRoot) : null,
                    game.AppId == GameCatalog.Ck3.AppId ? nativeSteam : null,
                    new InstalledModRemover(game.AppId, downloadStore, nativeSteam is null ? null : nativeSteam.UnsubscribeAsync),
                    installedContent,
                    fileConflicts: game.AppId == Ck3.AppId ? new Ck3FileConflictScanner() : null,
                    localModInstaller: game.AppId == Ck3.AppId ? new Ck3LocalModInstaller() : null,
                    diagnostics: game.AppId == Ck3.AppId ? new Ck3DiagnosticsReader() : null);
                WorkshopDownloadService? downloads = null;
                if (nativeSteam is not null)
                {
                    downloads = new WorkshopDownloadService(nativeSteam, downloadStore);
                    downloadServices.Add(downloads);
                }
                return new MainWindowViewModel(workspace,
                    new WorkshopService(new SteamWorkshopSource(http, game),
                        new SteamWorkshopBrowser(http, cacheDuration: TimeSpan.Zero, game: game), images, game,
                        cache: new FileWorkshopCacheStore(gameRoot, game: game)), new ExternalNavigator(game),
                    localModMonitor: new FileSystemLocalModMonitor(locator, scanner), downloads: downloads, dependencySource: workshopDependencies, uiLanguage: language);
            }, discoverGames: gameDiscovery.DiscoverAsync);
            GameTheme.Apply(workspaces.Current.Game);
            var window = new MainWindow { DataContext = workspaces.Current };
            workspaces.WorkspaceChanged += workspace =>
            {
                window.DataContext = workspace;
                GameTheme.Apply(workspace.Game);
            };
            window.Opened += async (_, _) => await workspaces.InitializeAsync();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                workspaces.Dispose();
                foreach (var downloads in downloadServices) downloads.Dispose();
                http.Dispose(); imageTransport.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
