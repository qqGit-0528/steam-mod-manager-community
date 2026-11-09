using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>只读取 Steam 官方基础元数据，不下载或执行 Mod 文件。</summary>
public sealed class SteamWorkshopSource(HttpClient httpClient, GameDefinition? game = null) : IWorkshopSource
{
    private readonly GameDefinition _game = game ?? GameCatalog.Ck3;
    public static readonly Uri Endpoint = new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");

    public async Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
    {
        if (publishedFileId == 0) throw new ArgumentOutOfRangeException(nameof(publishedFileId), T("WorkshopItemIDsMustBeGreaterThan"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = publishedFileId.ToString(CultureInfo.InvariantCulture)
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(T("SteamRequestFailedHTTPTryAgainLater", (int)response.StatusCode), null, response.StatusCode);
            var bytes = await BoundedHttp.ReadAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("response", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("publishedfiledetails", out var details) || details.ValueKind != JsonValueKind.Array || details.GetArrayLength() == 0)
                throw new InvalidDataException(T("SteamReturnedIncompleteDetailsForThisWorkshop"));
            var item = details[0];
            var result = Integer(item, "result");
            if (result is 9 or 15)
                throw new WorkshopItemUnavailableException(publishedFileId, T("ThisWorkshopItemWasDeletedOrIs", result));
            if (result != 1)
                throw new InvalidDataException(T("SteamCouldNotReadThisItemResult", result));
            var id = Unsigned(item, "publishedfileid");
            if (id != publishedFileId) throw new InvalidDataException(T("SteamReturnedADifferentItemIDThan"));
            var appId = Unsigned(item, "consumer_app_id");
            if (appId != _game.AppId) throw new InvalidDataException(T("ThisItemBelongsToAppRatherThan", appId, _game.ShortName, _game.AppId));
            var title = RequiredText(item, "title");
            var updated = Integer(item, "time_updated");
            var size = Integer(item, "file_size");
            if (size < 0) throw new InvalidDataException(T("SteamReturnedAnInvalidFileSize"));
            DateTimeOffset date;
            try { date = DateTimeOffset.FromUnixTimeSeconds(updated); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException(T("SteamReturnedAnInvalidUpdateTime"), ex); }
            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array)
                foreach (var tag in tagList.EnumerateArray())
                    if (OptionalText(tag, "tag") is { Length: > 0 } value) tags.Add(value);
            var preview = OptionalText(item, "preview_url");
            if (!Uri.TryCreate(preview, UriKind.Absolute, out var previewUri) || (previewUri.Scheme != "https" && previewUri.Scheme != "http")) preview = null;
            return new WorkshopItem(id, _game.AppId, title, OptionalText(item, "description") ?? "", preview,
                new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"), date, size, tags, null,
                CreatorId: OptionalText(item, "creator"));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(T("TheSteamRequestTimedOutCheckThe"), ex); }
        catch (JsonException ex)
        { throw new InvalidDataException(T("TheSteamJSONCouldNotBeParsed"), ex); }
    }

    private static string RequiredText(JsonElement element, string property) =>
        OptionalText(element, property) is { Length: > 0 } value ? value : throw new InvalidDataException(T("SteamDataIsMissingRequiredField", property));
    private static string? OptionalText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Integer(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new InvalidDataException(T("SteamDataHasAMissingOrInvalid", property));
    }
    private static ulong Unsigned(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new InvalidDataException(T("SteamDataHasAMissingOrInvalid", property));
    }
}
