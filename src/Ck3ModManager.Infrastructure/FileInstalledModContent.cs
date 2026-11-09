using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class FileInstalledModContent : IInstalledModContent
{
    public LocalInstallation Inspect(string path, bool requirePayload)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                return !requirePayload || Directory.EnumerateFileSystemEntries(path).Any()
                    ? new(LocalInstallationState.Installed, path) : new(LocalInstallationState.Missing);

            var file = new FileInfo(path);
            return file.Exists && (!requirePayload || file.Length > 0)
                ? new(LocalInstallationState.Installed, file.DirectoryName) : new(LocalInstallationState.Missing);
        }
        catch (FileNotFoundException) { return new(LocalInstallationState.Missing); }
        catch (DirectoryNotFoundException) { return new(LocalInstallationState.Missing); }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return new(LocalInstallationState.Unknown); }
    }
}
