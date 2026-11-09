using static Ck3ModManager.Core.AppText;
using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>读取监狱建筑师的 mods 子目录与 manifest.txt。</summary>
public sealed class PrisonArchitectModScanner(FileWorkshopDownloadStore? store = null) : IModScanner
{
    public Task<ModScanResult> ScanAsync(GameEnvironment environment, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(environment.LocalModDirectories, environment.WorkshopDirectories, cancellationToken, environment.GameDirectory), cancellationToken);

    public Task<ModScanResult> ScanAsync(string userDataDirectory, IReadOnlyList<string> workshopDirectories,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan([Path.Combine(userDataDirectory, "mods")], workshopDirectories, cancellationToken), cancellationToken);

    private ModScanResult Scan(IReadOnlyList<string> localDirectories, IReadOnlyList<string> workshopDirectories,
        CancellationToken token, string? gameDirectory = null)
    {
        var warnings = new List<string>();
        var mods = new Dictionary<string, InstalledMod>(StringComparer.Ordinal);
        foreach (var root in localDirectories.Distinct(SteamGameLocator.PathComparer))
            ScanDirectory(root, false, mods, warnings, token);
        foreach (var root in workshopDirectories.Distinct(SteamGameLocator.PathComparer))
            ScanDirectory(root, true, mods, warnings, token);
        var locations = new ModInstallationClassifier(gameDirectory, localDirectories, workshopDirectories, store, GameCatalog.PrisonArchitect.AppId);
        return new(mods.Values.Select(locations.Classify).OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Id, StringComparer.Ordinal).ToArray(), warnings);
    }

    private static void ScanDirectory(string root, bool workshop, Dictionary<string, InstalledMod> mods,
        List<string> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var knownId = workshop ? ParseId(Path.GetFileName(folder)) : null;
                if (workshop && knownId is null) continue;
                var manifest = Path.Combine(folder, "manifest.txt");
                if (!File.Exists(manifest)) continue;
                try
                {
                    var fields = TextDataParser.Read(TextDataParser.ReadSmallFile(manifest));
                    var name = fields.Value("Name");
                    if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException(T("ManifestTxtIsMissingName"));
                    var itemId = knownId ?? ParseId(fields.Value("FileID"));
                    var id = Ck3ModScanner.StableLocalId(folder);
                    mods[id] = new InstalledMod(id, name, folder, manifest, fields.Value("Version"), null, itemId, [],
                        itemId is null ? ModOrigin.Local : ModOrigin.SteamWorkshop, true);
                }
                catch (Exception error) when (SteamGameLocator.IsReadableError(error) || error is InvalidDataException)
                {
                    warnings.Add(T("CouldNotReadThePrisonArchitectMod", manifest, error.Message));
                }
            }
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error))
        {
            warnings.Add(T("CouldNotScanThePrisonArchitectMod", root, error.Message));
        }
    }

    private static ulong? ParseId(string? text) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
