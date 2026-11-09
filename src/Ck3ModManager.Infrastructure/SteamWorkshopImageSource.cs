using Ck3ModManager.Core;
using System.Net;
using System.Net.Http.Headers;

namespace Ck3ModManager.Infrastructure;

/// <summary>Downloads only Steam-hosted preview images; redirects are validated before following.</summary>
public sealed class SteamWorkshopImageSource : IWorkshopImageSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _slots = new(4);

    public SteamWorkshopImageSource()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        _ownsClient = true;
    }

    // A supplied client must also have automatic redirects disabled.
    public SteamWorkshopImageSource(HttpClient http, TimeSpan? requestTimeout = null)
    {
        _http = http;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public static bool IsAllowedUrl(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host == "steamusercontent.com" || uri.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "steamuserimages-a.akamaihd.net" || uri.Host == "cdn.akamai.steamstatic.com" || uri.Host == "shared.akamai.steamstatic.com");

    public async Task<byte[]> GetPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await FetchAsync(url, null, null, cancellationToken).ConfigureAwait(false);
        return result.Bytes ?? throw new HttpRequestException("Steam 图片未返回正文。");
    }

    internal async Task<PreviewFetchResult> FetchAsync(string url, string? entityTag,
        DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedUrl(uri))
            throw new InvalidOperationException("封面地址不是受支持的 Steam 图片来源。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        await _slots.WaitAsync(timeout.Token);
        try
        {
            for (var redirect = 0; redirect < 4; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // 跳转目标可能是另一个表示，不能沿用原 URL 的验证器。
                if (redirect == 0 && EntityTagHeaderValue.TryParse(entityTag, out var parsedTag)) request.Headers.IfNoneMatch.Add(parsedTag);
                else if (redirect == 0 && lastModified is not null) request.Headers.IfModifiedSince = lastModified;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var headers = PreviewCacheHeaders.Read(response);
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new PreviewFetchResult(null, true, headers);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var target = response.Headers.Location;
                    if (target is null) break;
                    uri = target.IsAbsoluteUri ? target : new Uri(uri, target);
                    if (!IsAllowedUrl(uri)) throw new InvalidOperationException("Steam 封面跳转到了不受支持的来源。");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    throw new HttpRequestException("Steam 封面响应不是图片。");
                return new PreviewFetchResult(await BoundedHttp.ReadAsync(response.Content, 5 * 1024 * 1024, timeout.Token).ConfigureAwait(false), false, headers);
            }
            throw new HttpRequestException("Steam 封面跳转次数过多。");
        }
        finally { _slots.Release(); }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

internal sealed record PreviewFetchResult(byte[]? Bytes, bool NotModified, PreviewCacheHeaders Headers);

internal sealed record PreviewCacheHeaders(bool? NoStore, bool? NoCache, bool? MustRevalidate,
    TimeSpan? MaxAge, DateTimeOffset? Expires, DateTimeOffset? Date, TimeSpan? Age,
    string? EntityTag, DateTimeOffset? LastModified)
{
    internal static PreviewCacheHeaders Read(HttpResponseMessage response)
    {
        var control = response.Headers.CacheControl;
        return new(control?.NoStore, control?.NoCache, control?.MustRevalidate, control?.MaxAge,
            response.Content.Headers.Expires, response.Headers.Date, response.Headers.Age,
            response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
    }
}
