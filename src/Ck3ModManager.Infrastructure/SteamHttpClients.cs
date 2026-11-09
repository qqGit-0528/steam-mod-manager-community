using System.Net;

namespace Ck3ModManager.Infrastructure;

public static class SteamHttpClients
{
    public static HttpClient CreateMetadataClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ck3ModManager/0.1");
        return client;
    }
}
