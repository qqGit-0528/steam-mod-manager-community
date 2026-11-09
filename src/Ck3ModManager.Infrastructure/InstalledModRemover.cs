using static Ck3ModManager.Core.AppText;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>仅删除经重新扫描确认、位于当前游戏管理目录内的单个 Mod。</summary>
public sealed class InstalledModRemover(uint appId, FileWorkshopDownloadStore store,
    Func<GameEnvironment, ulong, CancellationToken, Task>? unsubscribe) : IInstalledModRemover
{
    public async Task RemoveAsync(GameEnvironment environment, InstalledMod mod, CancellationToken cancellationToken)
    {
        if (!mod.IsAvailable || !Path.IsPathFullyQualified(mod.ContentPath))
            throw new InvalidOperationException(T("ThisModHasNoLocalInstallationThat"));

        var path = Normalize(mod.ContentPath);
        var classifier = new ModInstallationClassifier(environment.GameDirectory, environment.LocalModDirectories,
            environment.WorkshopDirectories, store, appId);
        var (management, root) = classifier.Locate(path, mod.WorkshopId);
        if (management == ModInstallManagement.External || management != mod.Management)
            throw new InvalidOperationException(T("TheAppCannotManageThisInstallationOr"));
        if (management == ModInstallManagement.Steam && unsubscribe is null)
            throw new NotSupportedException(T("UseTheSteamClientToUnsubscribeAnd"));

        if (root is not null) EnsureOrdinary(root);
        EnsureOrdinary(path);
        var itemId = mod.WorkshopId.GetValueOrDefault();
        var managed = management == ModInstallManagement.Application;
        // 在取消订阅前验证目标。跨实例锁覆盖订阅变更和文件删除，避免本应用同时重装。
        using var itemLock = management == ModInstallManagement.Steam || managed
            ? await store.AcquireAsync(appId, itemId, cancellationToken).ConfigureAwait(false) : null;
        if (managed) store.EnsureManagedRevisionHasNoLinks(path);
        if (management == ModInstallManagement.Steam)
            await unsubscribe!(environment, itemId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            var descriptors = FindWrappers(classifier.LocalRoots, path);
            if (root is not null && Directory.Exists(root)) EnsureOrdinary(root);
            if (managed) store.EnsureManagedRevisionHasNoLinks(path);
            DeleteContent(path);
            foreach (var descriptor in descriptors)
                File.Delete(descriptor);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string[] FindWrappers(IEnumerable<string> roots, string content)
    {
        var matches = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            var dataRoot = Path.GetDirectoryName(root) ?? root;
            foreach (var descriptor in Directory.EnumerateFiles(root, "*.mod"))
            {
                try
                {
                    if ((File.GetAttributes(descriptor) & FileAttributes.ReparsePoint) != 0) continue;
                    var properties = TextDataParser.Read(TextDataParser.ReadSmallFile(descriptor));
                    var value = properties.Value("path") ?? properties.Value("archive");
                    var target = string.IsNullOrWhiteSpace(value)
                        ? Path.Combine(root, Path.GetFileNameWithoutExtension(descriptor))
                        : Ck3ModScanner.ResolvePath(value, descriptor, dataRoot);
                    if (SteamGameLocator.PathComparer.Equals(content, Normalize(target))) matches.Add(descriptor);
                }
                catch (Exception error) when (SteamGameLocator.IsReadableError(error) || error is InvalidDataException) { }
            }
        }
        return matches.ToArray();
    }

    private static void DeleteContent(string path)
    {
        try
        {
            var attributes = EnsureOrdinary(path);
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        // Steam may already have removed the item after acknowledging the unsubscribe.
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static FileAttributes EnsureOrdinary(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(T("ModInstallationsAccessedThroughSymbolicLinksCannot"));
        return attributes;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

}
