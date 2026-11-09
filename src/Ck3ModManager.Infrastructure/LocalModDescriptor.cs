using System.Globalization;
using System.Text;

namespace Ck3ModManager.Infrastructure;

internal static class LocalModDescriptor
{
    private static readonly HashSet<string> IdentityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "remote_file_id", "path", "archive"
    };

    internal static string Write(IReadOnlyList<TextProperty> properties, string? field = null,
        string? contentPath = null, ulong? workshopId = null)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var builder = new StringBuilder();
        foreach (var property in properties.Where(property => !IdentityFields.Contains(property.Name)))
        {
            if (property.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_')) throw new InvalidDataException("Mod 描述属性名不受支持。");
            builder.Append(property.Name).Append('=');
            if (property.Block is { } block)
                builder.Append("{ ").AppendJoin(' ', block.Select(token => token is "{" or "}" or "=" ? token : Quote(token))).Append(" }");
            else builder.Append(Quote(property.Scalar ?? ""));
            builder.AppendLine();
        }
        if (field is not null && contentPath is not null)
            builder.Append(field).Append('=').AppendLine(Quote(contentPath));
        if (workshopId is { } id)
            builder.Append("remote_file_id=").AppendLine(Quote(id.ToString(CultureInfo.InvariantCulture)));
        return builder.ToString();
    }
}
