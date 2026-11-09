using static Ck3ModManager.Core.AppText;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed partial class WorkspaceService
{
    public bool HasLocalModImport => localModInstaller is not null;
    public bool HasPlaysetPackageTransfer => playsetSnapshots is IPlaysetPackageStore;

    public async Task<LocalModImportPreview> PreviewLocalModImportAsync(AppSettings settings, string sourcePath, CancellationToken token = default)
    {
        var installer = localModInstaller ?? throw new NotSupportedException(T("ThisGameDoesNotSupportLocalMod"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), token);
        return await installer.PreviewAsync(environment, sourcePath, token);
    }

    public async Task<string> InstallLocalModAsync(AppSettings settings, LocalModImportPreview preview, CancellationToken token = default)
    {
        var installer = localModInstaller ?? throw new NotSupportedException(T("ThisGameDoesNotSupportLocalMod"));
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), token);
        return await installer.InstallAsync(environment, preview, token);
    }

    public async Task ExportPlaysetPackageAsync(AppSettings settings, ModProfile profile, string destinationPath, CancellationToken token = default)
    {
        var packages = PlaysetPackages();
        var (environment, scan) = await DiscoverAndScanAsync(settings, token);
        await playsetSnapshots!.ValidateGameVersionAsync(environment, profile.Id, token);
        ValidateEnabledMods(profile, scan.Mods);
        await packages.ExportPackageAsync(environment, profile, scan.Mods, destinationPath, token);
    }

    public Task<PlaysetPackagePreview> PreviewPlaysetPackageAsync(string sourcePath, CancellationToken token = default) =>
        PlaysetPackages().PreviewPackageAsync(sourcePath, token);

    public async Task<ModProfile> ImportPlaysetPackageAsync(AppSettings settings, PlaysetPackagePreview preview, CancellationToken token = default)
    {
        var packages = PlaysetPackages();
        var environment = await gameLocator.DiscoverAsync(settings.ToDiscoveryOptions(), token);
        var profile = await packages.ImportPackageAsync(environment, preview, token);
        try
        {
            await SaveProfileAsync(profile, token);
        }
        catch (Exception saveError)
        {
            try { await packages.RemoveImportedPackageAsync(environment, profile.Id); }
            catch (Exception rollbackError)
            {
                throw new AggregateException(T("TheRestoredProfileCouldNotBeSaved"), saveError, rollbackError);
            }
            throw;
        }
        return profile;
    }

    private IPlaysetPackageStore PlaysetPackages() => playsetSnapshots as IPlaysetPackageStore
        ?? throw new NotSupportedException(T("ThisGameDoesNotSupportPlaysetPacks"));
}
