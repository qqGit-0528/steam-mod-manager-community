using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

if (SteamClientWorker.IsWorker(args)) return SteamClientWorker.Run(args);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

string? gameId = null;
if (args.FirstOrDefault() == "--game")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("请在 --game 后指定游戏 ID，使用 games 查看支持的游戏。");
        return 2;
    }
    gameId = args[1].Trim();
    args = args[2..];
}

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine("""
        Steam Mod Manager · 诊断工具

        games                   列出支持的游戏及其 ID
        doctor                  发现游戏目录并扫描本地 Mod，不修改游戏文件
        workshop <工坊ID或链接>  查询 Steam 创意工坊公开信息
        browse [搜索词]         搜索 Steam 工坊公开列表（第一页）
        profiles                列出已保存的配置清单
        download <工坊ID>       通过本机 Steam 订阅、下载并跟踪真实进度

        在命令前加 --game <游戏ID> 选择游戏，默认 ck3。
        示例：--game hoi4 doctor，--game stellaris browse 汉化
        下载复用已登录的本机 Steam 客户端，内容由 Steam 安装和更新；无需本应用扫码。
        """);
    return 0;
}

var rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ck3ModManager");
if (args[0] == "download" && Array.IndexOf(args, "--storage") is var storageIndex && storageIndex >= 0)
{
    if (storageIndex + 1 >= args.Length) { Console.Error.WriteLine("--storage 后需要指定目录。"); return 2; }
    rootDirectory = Path.GetFullPath(args[storageIndex + 1]);
}
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() }
};

try
{
    using var http = SteamHttpClients.CreateMetadataClient();
    var discovery = new SteamGameDiscovery(http, settingsDirectory: rootDirectory);
    IReadOnlyList<GameDefinition>? games = null;
    if (args is ["games"] || gameId?.StartsWith("steam-", StringComparison.OrdinalIgnoreCase) == true)
    {
        var steamDirectory = (await new JsonSettingsStore(rootDirectory).LoadAsync(cancellation.Token)).SteamDirectory;
        games = await discovery.DiscoverAsync(steamDirectory, cancellation.Token);
    }
    var game = gameId is null ? GameCatalog.Ck3 : (games ?? GameCatalog.All).FirstOrDefault(item =>
        item.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));
    if (game is null)
    {
        Console.Error.WriteLine($"不支持的游戏：{gameId}");
        return 2;
    }
    if (args is ["games"])
    {
        Console.WriteLine(JsonSerializer.Serialize(games!.Select(item => new { item.Id, item.DisplayName, item.ShortName, item.AppId }), json));
        return 0;
    }
    var dataDirectory = GameStorage.GetDirectory(rootDirectory, game);
    var profileStore = new JsonProfileStore(dataDirectory);
    var downloadStore = new FileWorkshopDownloadStore(rootDirectory);
    IModScanner scanner = game.AppId == GameCatalog.PrisonArchitect.AppId
        ? new PrisonArchitectModScanner(downloadStore) : new Ck3ModScanner(game.IsDiscovered, downloadStore, game.AppId);
    var workspace = new WorkspaceService(new SteamGameLocator(game), scanner, profileStore, new JsonSettingsStore(dataDirectory));
    var workshop = new WorkshopService(new SteamWorkshopSource(http, game),
        new SteamWorkshopBrowser(http, TimeSpan.Zero, game: game), game: game,
        cache: new FileWorkshopCacheStore(dataDirectory, game: game));
    object result;
    switch (args[0])
    {
        case "doctor" when args.Length == 1:
            result = await workspace.InitializeAsync(cancellation.Token);
            break;
        case "workshop" when args.Length == 2:
            result = await workshop.LookupAsync(args[1], cancellation.Token);
            break;
        case "browse" when args.Length <= 2:
            var page = await workshop.SearchAsync(new WorkshopSearchRequest(args.ElementAtOrDefault(1) ?? ""), cancellation.Token);
            result = new { page.Page, page.TotalPages, page.TotalCount, page.CategoryTags, page.VersionTags,
                Items = page.Items.Select(item => new { item.PublishedFileId, item.Title, item.PreviewImageUrl, item.UpdatedAt, item.Subscriptions }) };
            break;
        case "download" when args.Length >= 2:
            if (!game.SupportsNativeWorkshopDownloads)
                throw new NotSupportedException("此游戏的工坊内容请通过 Steam 客户端下载。");
            var reference = WorkshopReference.Parse(args[1]);
            var item = await workshop.LookupAsync(reference.PublishedFileId.ToString(), cancellation.Token);
            if (args.Contains("--output") || args.Contains("--no-login"))
                throw new ArgumentException("下载现由本机 Steam 管理，不能指定独立安装目录或独立登录选项。");
            var environment = await new SteamGameLocator(game).DiscoverAsync(new DiscoveryOptions(), cancellation.Token);
            var nativeDownloads = new SteamClientWorkshopDownloader(game, new SteamGameLocator(game), new JsonSettingsStore(dataDirectory), rootDirectory);
            using (var downloads = new WorkshopDownloadService(nativeDownloads, downloadStore))
            {
                downloads.Changed += snapshot =>
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(snapshot, json));
                    var limitIndex = Array.IndexOf(args, "--max-bytes");
                    if (limitIndex >= 0 && limitIndex + 1 < args.Length && long.TryParse(args[limitIndex + 1], out var limit) && snapshot.TotalBytes > limit)
                        downloads.Tasks.FirstOrDefault(active => active.Snapshot.TaskId == snapshot.TaskId)?.Cancel();
                };
                await downloads.ReadHistoryAsync(cancellation.Token);
                var task = downloads.Start(item, environment.UserDataDirectory);
                using var registration = cancellation.Token.Register(task.Cancel);
                await task.Completion;
                Console.WriteLine(JsonSerializer.Serialize(task.Snapshot, json));
                return task.Snapshot.State == WorkshopDownloadState.Completed ? 0 : task.Snapshot.State is WorkshopDownloadState.Cancelled or WorkshopDownloadState.Interrupted ? 130 : 1;
            }
        case "profiles" when args.Length == 1:
            result = await profileStore.LoadAsync(cancellation.Token);
            break;
        default:
            Console.Error.WriteLine("命令或参数不正确，请使用 --help 查看用法。");
            return 2;
    }
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("操作已取消。");
    return 130;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"操作未完成：{exception.Message}");
    return 1;
}
