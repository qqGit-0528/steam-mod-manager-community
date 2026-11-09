using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed partial class SteamWorkshopDependencySource(HttpClient http) : IWorkshopDependencySource
{
    public async Task<IReadOnlyList<ulong>?> ReadRequiredItemsAsync(uint appId, ulong itemId,
        CancellationToken cancellationToken = default)
    {
        if (appId == 0 || itemId == 0) return null;
        var address = new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={itemId.ToString(CultureInfo.InvariantCulture)}&l=english");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.RequestMessage?.RequestUri is { } actual &&
                (actual.Scheme != Uri.UriSchemeHttps || actual.Host != address.Host)) return null;
            var bytes = await BoundedHttp.ReadAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            return ReadPage(Encoding.UTF8.GetString(bytes), appId, itemId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or RegexMatchTimeoutException)
        { return null; }
    }

    private static IReadOnlyList<ulong>? ReadPage(string html, uint appId, ulong itemId)
    {
        var page = ActiveDescription().Match(html);
        if (!page.Success || !ulong.TryParse(page.Groups["id"].ValueSpan, out var actualId) || actualId != itemId)
            return null;
        var appMarker = $"id=\"sharedfiles_content_ctn\" data-miniprofile-appid=\"{appId.ToString(CultureInfo.InvariantCulture)}\"";
        var app = html.IndexOf(appMarker, page.Index, StringComparison.Ordinal);
        if (app < 0) return null;
        var side = html.IndexOf("id=\"rightContents\"", app, StringComparison.Ordinal);
        if (side < 0) return null;
        var end = html.IndexOf(">Created by</div>", side, StringComparison.Ordinal);
        if (end < 0) return null;

        var sidebar = html[side..end];
        const string marker = "id=\"RequiredItems\"";
        var required = sidebar.IndexOf(marker, StringComparison.Ordinal);
        if (required < 0)
            return sidebar.Contains("requiredItemsContainer", StringComparison.Ordinal) ? null : [];
        var contentStart = sidebar.IndexOf('>', required + marker.Length) + 1;
        if (contentStart <= 0) return null;
        var ids = new List<ulong>();
        var section = sidebar[contentStart..].TrimStart();
        while (!section.StartsWith("</div>", StringComparison.Ordinal))
        {
            var entry = RequiredItem().Match(section);
            if (!entry.Success || !ulong.TryParse(entry.Groups["id"].ValueSpan, out var id) || id == 0) return null;
            ids.Add(id);
            if (ids.Count > 128) return null;
            section = section[entry.Length..].TrimStart();
        }
        return ids.Distinct().ToArray();
    }

    [GeneratedRegex("""<a\s+href="https://steamcommunity\.com/sharedfiles/filedetails/\?id=(?<id>[0-9]+)"\s+class="sectionTab\s+active\s+description">""",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex ActiveDescription();

    [GeneratedRegex("""\A<a\s+href="https://steamcommunity\.com/(?:workshop|sharedfiles)/filedetails/\?id=(?<id>[0-9]+)"[^>]*>\s*<div\s+class="requiredItem">[^<]*</div>\s*</a>""",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex RequiredItem();
}
