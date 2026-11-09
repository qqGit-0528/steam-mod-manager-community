using static Ck3ModManager.Core.AppText;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Ck3ModManager.Core;

/// <summary>经过验证的工坊条目引用。解析不执行网络请求。</summary>
public sealed record WorkshopReference
{
    public ulong PublishedFileId { get; }
    public Uri WorkshopUrl => new($"https://steamcommunity.com/sharedfiles/filedetails/?id={PublishedFileId}");

    private WorkshopReference(ulong publishedFileId) => PublishedFileId = publishedFileId;

    public static WorkshopReference Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return TryParse(input, out var reference)
            ? reference
            : throw new FormatException(T("EnterANonzeroWorkshopIDOrA"));
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out WorkshopReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var value = input.Trim();
        if (TryParseId(value, out var id))
        {
            reference = new WorkshopReference(id);
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.AbsolutePath is not ("/sharedfiles/filedetails" or "/sharedfiles/filedetails/"
                or "/workshop/filedetails" or "/workshop/filedetails/")) return false;

        string? idValue = null;
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (pair[0] != "id") continue;
            // 多个 id 即使相同也拒绝，避免不同消费者采用不同参数。
            if (idValue is not null || pair.Length != 2) return false;
            idValue = pair[1];
        }
        if (!TryParseId(idValue, out id)) return false;
        reference = new WorkshopReference(id);
        return true;
    }

    private static bool TryParseId(string? value, out ulong id)
    {
        id = 0;
        return !string.IsNullOrEmpty(value) && value.All(character => character is >= '0' and <= '9')
            && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}
