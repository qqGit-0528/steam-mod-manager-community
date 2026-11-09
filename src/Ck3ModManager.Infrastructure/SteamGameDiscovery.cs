using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>发现当前已安装且有本地 Mod 目录或 Steam 创意工坊的游戏。</summary>
public sealed class SteamGameDiscovery(HttpClient http, IEnumerable<string>? steamRoots = null, string? homeDirectory = null, string? settingsDirectory = null)
{
    private const int SteamWorkshopCategory = 30;
    private readonly SteamGameLocator _locator = new(steamRoots, homeDirectory);
    private readonly ConcurrentDictionary<uint, (bool HasWorkshop, DateTimeOffset CheckedAt)> _workshop = new();

    public async Task<IReadOnlyList<GameDefinition>> DiscoverAsync(string? steamDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = await Task.Run(() => FindInstalled(steamDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
        var discovered = new ConcurrentBag<GameDefinition>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (candidate, token) =>
        {
            var (game, hasWorkshopFiles) = candidate;
            var hasWorkshop = hasWorkshopFiles || await HasWorkshopAsync(game.AppId, token).ConfigureAwait(false);
            // 只加入支持创意工坊的游戏；没有工坊的已安装游戏不进入列表。
            if (hasWorkshop) discovered.Add(game with { HasSteamWorkshop = true });
        }).ConfigureAwait(false);
        return GameCatalog.All.Concat(discovered.OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.AppId)).ToArray();
    }

    private IReadOnlyList<(GameDefinition Game, bool HasWorkshopFiles)> FindInstalled(
        string? steamDirectory, CancellationToken cancellationToken)
    {
        var roots = new[] { steamDirectory }.Concat(ReadSavedSteamDirectories()).Distinct(SteamGameLocator.PathComparer);
        var libraries = roots.SelectMany(root => _locator.FindLibraries(root, [], cancellationToken).Libraries)
            .Distinct(SteamGameLocator.PathComparer).ToArray();
        var candidates = new List<(GameDefinition, bool)>();
        var seen = GameCatalog.All.Select(game => game.AppId).ToHashSet();
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            string[] manifests;
            try { manifests = Directory.Exists(steamApps) ? Directory.GetFiles(steamApps, "appmanifest_*.acf") : []; }
            catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { continue; }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = Path.GetFileNameWithoutExtension(manifest).AsSpan("appmanifest_".Length);
                if (!uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0 || seen.Contains(appId))
                    continue;
                try
                {
                    var install = SteamGameLocator.ReadInstallation(library, manifest, appId);
                    if (!install.FullyInstalled || !install.HasContent) continue;

                    var id = FormattableString.Invariant($"steam-{appId}");
                    var game = new GameDefinition(id, appId, install.Name, install.Name, install.Name, Path.GetFileName(install.Directory))
                    {
                        IsDiscovered = true,
                        RequiresDeclaredWorkshopCompatibility = false,
                        SteamLibraryHint = library,
                        SteamLibraryRoots = libraries
                    };
                    var hasWorkshopFiles = libraries.Any(path => HasWorkshopFiles(path, appId));
                    candidates.Add((game, hasWorkshopFiles));
                    seen.Add(appId);
                }
                catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { }
            }
        }
        return candidates;
    }

    private IEnumerable<string> ReadSavedSteamDirectories()
    {
        if (settingsDirectory is null) yield break;
        string[] directories;
        try
        {
            var games = Path.Combine(settingsDirectory, "games");
            directories = new[] { settingsDirectory }.Concat(Directory.Exists(games)
                ? Directory.GetDirectories(games).Take(256) : []).ToArray();
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { yield break; }
        foreach (var directory in directories)
        {
            string? saved = null;
            try
            {
                var path = Path.Combine(directory, "settings.json");
                if (!File.Exists(path)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(TextDataParser.ReadSmallFile(path, 65_536), JsonFiles.Options);
                if (!string.IsNullOrWhiteSpace(settings?.SteamDirectory)) saved = Path.GetFullPath(settings.SteamDirectory);
            }
            catch (Exception error) when (error is JsonException || SteamGameLocator.IsReadableError(error)) { }
            if (saved is not null) yield return saved;
        }
    }

    private static bool HasWorkshopFiles(string library, uint appId)
    {
        if (Directory.Exists(SteamGameLocator.WorkshopPath(library, appId))) return true;
        var manifest = Path.Combine(library, "steamapps", "workshop", FormattableString.Invariant($"appworkshop_{appId}.acf"));
        if (!File.Exists(manifest)) return false;
        try
        {
            var app = TextDataParser.Read(TextDataParser.ReadSmallFile(manifest))
                .FirstOrDefault(node => node.Name.Equals("AppWorkshop", StringComparison.OrdinalIgnoreCase))?.Block;
            return app is not null && TextDataParser.ReadTokens(app).Value("appid") == appId.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return false; }
    }

    private async Task<bool> HasWorkshopAsync(uint appId, CancellationToken cancellationToken)
    {
        if (_workshop.TryGetValue(appId, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromMinutes(15))
            return cached.HasWorkshop;

        var id = appId.ToString(CultureInfo.InvariantCulture);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=categories,basic&l=english",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await BoundedHttp.ReadAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty(id, out var result) ||
                !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !result.TryGetProperty("data", out var data)) return cached.HasWorkshop;

            var hasWorkshop = data.TryGetProperty("type", out var type) && type.GetString() == "game" &&
                data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array &&
                categories.EnumerateArray().Any(category => category.TryGetProperty("id", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == SteamWorkshopCategory);
            _workshop[appId] = (hasWorkshop, DateTimeOffset.UtcNow);
            return hasWorkshop;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return cached.HasWorkshop; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException)
        { return cached.HasWorkshop; }
    }
}
