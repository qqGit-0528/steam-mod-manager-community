namespace Ck3ModManager.Core;

public interface IWorkshopSource
{
    Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default);
}

public interface IWorkshopBrowser
{
    Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default);
}

public interface IWorkshopImageSource
{
    Task<byte[]> GetPreviewAsync(string url, CancellationToken cancellationToken = default);
}

public interface IGameLocator
{
    Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default);
}

public interface IModScanner
{
    Task<ModScanResult> ScanAsync(GameEnvironment environment, CancellationToken cancellationToken = default) =>
        ScanAsync(environment.UserDataDirectory, environment.WorkshopDirectories, cancellationToken);

    Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
        CancellationToken cancellationToken = default);
}

public interface IInstalledModContent
{
    LocalInstallation Inspect(string path, bool requirePayload);
}

public interface IInstalledModRemover
{
    Task RemoveAsync(GameEnvironment environment, InstalledMod mod, CancellationToken cancellationToken);
}

public interface ILocalModMonitor
{
    IAsyncEnumerable<ModScanResult> WatchAsync(DiscoveryOptions options, CancellationToken cancellationToken = default);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IExternalNavigator
{
    void LaunchGame();
    void OpenWorkshop(ulong? publishedFileId = null);
    void OpenDirectory(string path);
}
