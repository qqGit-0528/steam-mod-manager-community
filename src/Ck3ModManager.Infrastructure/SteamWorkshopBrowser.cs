using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>Reads Steam's public, server-rendered Workshop list. This is a website adapter, not QueryFiles API.</summary>
public sealed class SteamWorkshopBrowser : IWorkshopBrowser
{
    private readonly HttpClient _http;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly GameDefinition _game;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset StoredAt, WorkshopBrowsePage Page)> _pages = [];
    private const int MaximumCachedPages = WorkshopCacheLimits.MemoryPages;

    public SteamWorkshopBrowser(HttpClient http, TimeSpan? cacheDuration = null, TimeProvider? timeProvider = null, GameDefinition? game = null)
    {
        _http = http;
        _game = game ?? GameCatalog.Ck3;
        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(2);
        if (_cacheDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheDuration));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkshopBrowsePage> SearchAsync(WorkshopSearchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = BuildUri(request, _game);
        lock (_cacheLock)
        {
            if (_pages.TryGetValue(uri.AbsoluteUri, out var cached) && _timeProvider.GetUtcNow() - cached.StoredAt < _cacheDuration)
                return cached.Page;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await BoundedHttp.ReadAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var page = ParsePage(System.Text.Encoding.UTF8.GetString(bytes), request.Page, _game);
        cancellationToken.ThrowIfCancellationRequested();
        if (_cacheDuration > TimeSpan.Zero)
            lock (_cacheLock)
            {
                _pages[uri.AbsoluteUri] = (_timeProvider.GetUtcNow(), page);
                if (_pages.Count > MaximumCachedPages)
                    _pages.Remove(_pages.MinBy(entry => entry.Value.StoredAt).Key);
            }
        return page;
    }

    public static Uri BuildUri(WorkshopSearchRequest request, GameDefinition? game = null)
    {
        game ??= GameCatalog.Ck3;
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request));
        var sort = request.Sort switch
        {
            WorkshopSort.Trending => "trend", WorkshopSort.MostSubscribed => "totaluniquesubscribers",
            WorkshopSort.MostRecent => "mostrecent", WorkshopSort.RecentlyUpdated => "lastupdated",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var query = $"appid={game.AppId}&section=readytouseitems&l=schinese&browsesort={sort}&p={request.Page}";
        if (!string.IsNullOrWhiteSpace(request.SearchText)) query += "&searchtext=" + Uri.EscapeDataString(request.SearchText.Trim());
        foreach (var tag in new[] { request.CategoryTag, request.VersionTag })
            if (!string.IsNullOrWhiteSpace(tag)) query += "&requiredtags%5B%5D=" + Uri.EscapeDataString(tag);
        return new Uri("https://steamcommunity.com/workshop/browse/?" + query);
    }

    public static WorkshopBrowsePage ParsePage(string html, int expectedPage, GameDefinition? game = null)
    {
        game ??= GameCatalog.Ck3;
        try
        {
            var script = Regex.Match(html, "<script\\b[^>]*\\bid=[\"']valve-ssr-data[\"'][^>]*>(.*?)</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!script.Success) throw new FormatException();
            using var document = JsonDocument.Parse(script.Groups[1].Value);
            var root = document.RootElement;
            using var queryData = JsonDocument.Parse(root.GetProperty("renderContext").GetProperty("queryData").GetString()!);
            var query = queryData.RootElement.GetProperty("queries").EnumerateArray().FirstOrDefault(q =>
                q.TryGetProperty("queryKey", out var key) && key.ValueKind == JsonValueKind.Array && key.GetArrayLength() >= 2 &&
                q.GetProperty("queryKey")[0].ValueKind == JsonValueKind.String &&
                q.GetProperty("queryKey")[0].GetString() == "workshop_browse" &&
                q.GetProperty("queryKey")[1].GetProperty("appid").GetUInt32() == game.AppId);
            if (query.ValueKind == JsonValueKind.Undefined) throw new FormatException();
            var data = query.GetProperty("state").GetProperty("data");
            if (data.GetProperty("eresult").GetInt32() != 1) throw new FormatException();
            var page = data.GetProperty("current_page").GetInt32();
            if (page != expectedPage) throw new FormatException();
            var totalCount = data.GetProperty("total_count").GetInt32();
            var totalPages = Math.Max(1, data.GetProperty("total_pages").GetInt32());
            if (totalCount < 0) throw new FormatException();
            var items = new List<WorkshopItem>();
            foreach (var item in data.GetProperty("results").EnumerateArray())
            {
                if (item.GetProperty("consumer_appid").GetUInt32() != game.AppId) throw new FormatException();
                var id = ulong.Parse(item.GetProperty("publishedfileid").GetString()!, CultureInfo.InvariantCulture);
                if (id == 0) throw new FormatException();
                var tags = item.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tag").GetString() ?? "").Where(t => t.Length > 0).ToArray();
                items.Add(new WorkshopItem(id, game.AppId,
                    WebUtility.HtmlDecode(item.GetProperty("title").GetString() ?? "未命名 Mod"),
                    item.TryGetProperty("short_description", out var description) ? description.GetString() ?? "" : "",
                    item.TryGetProperty("preview_url", out var preview) ? preview.GetString() : null,
                    new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"),
                    DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("time_updated").GetInt64()),
                    ReadLong(item, "file_size"), tags, null,
                    item.TryGetProperty("subscriptions", out _) ? ReadLong(item, "subscriptions") : null,
                    item.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.String ? creator.GetString() : null));
            }
            var categories = new HashSet<string>(StringComparer.Ordinal);
            var versions = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("loaderData", out var loaders))
                foreach (var loader in loaders.EnumerateArray())
                {
                    if (loader.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(loader.GetString())) continue;
                    using var loaderDoc = JsonDocument.Parse(loader.GetString()!);
                    if (!loaderDoc.RootElement.TryGetProperty("declaredTags", out var declared) ||
                        !declared.TryGetProperty("readytouse_tags", out var groups)) continue;
                    foreach (var group in groups.EnumerateArray())
                    {
                        var target = group.GetProperty("name").GetString() == "Compatible Version" ? versions : categories;
                        foreach (var tag in group.GetProperty("tags").EnumerateArray())
                            if (tag.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString())) target.Add(name.GetString()!);
                    }
                }
            foreach (var tag in items.SelectMany(i => i.Tags))
                if (Regex.IsMatch(tag, @"^\d+\.\d+(?:\s|\.|$)", RegexOptions.None, TimeSpan.FromSeconds(1))) versions.Add(tag);
            return new WorkshopBrowsePage(items, page, totalPages, totalCount,
                categories.Order(StringComparer.Ordinal).ToArray(), versions.OrderByDescending(VersionKey).ThenByDescending(v => v, StringComparer.Ordinal).ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException or RegexMatchTimeoutException)
        {
            throw new InvalidOperationException("无法读取 Steam 工坊列表，页面格式可能已更新或当前不可用。请重试，或在 Steam 中打开工坊。", error);
        }
    }

    private static Version VersionKey(string tag) =>
        Version.TryParse(Regex.Match(tag, @"^\d+(?:\.\d+)+", RegexOptions.None, TimeSpan.FromSeconds(1)).Value, out var version) ? version : new Version(0, 0);

    private static long ReadLong(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt64();
    }
}

internal static class BoundedHttp
{
    internal static async Task<byte[]> ReadAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new HttpRequestException("Steam 返回的内容过大。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) throw new HttpRequestException("Steam 返回的内容过大。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
