using static Ck3ModManager.Core.AppText;
using System.Text.Json;

namespace Ck3ModManager.Infrastructure;

internal sealed record SteamClientFrame(string Type, uint AppId, ulong ItemId,
    uint State = 0, long Bytes = 0, long? TotalBytes = null, string? ContentPath = null,
    long? SizeOnDisk = null, bool Reused = false, string? Error = null)
{
    internal const string Prefix = "CK3_STEAM_CLIENT_V1:";
    internal string Encode() => Prefix + JsonSerializer.Serialize(this);

    internal static SteamClientFrame? Decode(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (line.Length > 65536) throw new InvalidDataException(T("TheSteamBridgeMessageIsTooLarge"));
        try { return JsonSerializer.Deserialize<SteamClientFrame>(line[Prefix.Length..]) ?? throw new InvalidDataException(T("TheSteamBridgeMessageIsEmpty")); }
        catch (JsonException error) { throw new InvalidDataException(T("TheSteamBridgeMessageIsInvalid"), error); }
    }
}

internal sealed record SteamClientResult(string Kind, uint AppId, ulong ItemId, int Result);
internal sealed record SteamClientProgress(long Bytes, long? TotalBytes);
internal sealed record SteamClientInstalled(string Path, long Size);

internal interface ISteamClientApi : IDisposable
{
    uint AppId { get; }
    bool IsRunning { get; }
    uint GetItemState(ulong item);
    bool SubscribeItem(ulong item);
    bool UnsubscribeItem(ulong item);
    bool DownloadItem(ulong item);
    IReadOnlyList<SteamClientResult> RunFrame();
    SteamClientProgress? GetProgress(ulong item);
    SteamClientInstalled? GetInstalled(ulong item);
}
