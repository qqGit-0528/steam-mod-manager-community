using System.IO.Compression;
using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal static class Ck3SaveMetadataReader
{
    private const int Limit = 1024 * 1024;
    private static readonly Ck3SaveMetadata Unknown = new(null, null);

    internal static Ck3SaveMetadata Read(Stream stream, CancellationToken token)
    {
        var prefix = ReadPrefix(stream, token);
        if (Parse(prefix.Text, allowUnwrapped: false) is { } header) return header;

        try
        {
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var name in new[] { "meta", "gamestate" })
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(name);
                if (entry is null) continue;
                using var content = entry.Open();
                var text = ReadPrefix(content, token);
                if (Parse(text.Text, allowUnwrapped: name == "meta" && text.Complete) is { } metadata) return metadata;
            }
        }
        catch (InvalidDataException) { /* 二进制、损坏或无法解压的存档可以手动绑定。 */ }

        return Unknown;
    }

    private static (string Text, bool Complete) ReadPrefix(Stream stream, CancellationToken token)
    {
        var buffer = new byte[Limit + 1];
        var size = 0;
        while (size < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, size, buffer.Length - size);
            if (read == 0) break;
            size += read;
        }
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(size, Limit)), size <= Limit);
    }

    private static Ck3SaveMetadata? Parse(string text, bool allowUnwrapped)
    {
        var remaining = text.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart();
        if (remaining.StartsWith("SAV", StringComparison.Ordinal))
        {
            var newline = remaining.IndexOf('\n');
            if (newline < 0 || newline > 128) return null;
            remaining = remaining[(newline + 1)..].TrimStart();
        }
        if (remaining.StartsWith("meta_data", StringComparison.Ordinal))
        {
            remaining = remaining["meta_data".Length..].TrimStart();
            if (remaining.StartsWith("=")) remaining = remaining[1..].TrimStart();
            var length = BlockLength(remaining);
            if (length == 0) return null;
            remaining = remaining[1..(length - 1)];
        }
        else if (!allowUnwrapped) return null;

        try
        {
            var properties = TextDataParser.Read(remaining.ToString());
            var version = properties.Value("version");
            var mods = properties.LastOrDefault(property => property.Name == "mods")?.Block;
            return version is null && mods is null ? null : new(version, mods);
        }
        catch (FormatException) { return null; }
    }

    // 只把完整的元数据块交给词法器，存档正文可以很大或使用不同编码。
    private static int BlockLength(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] != '{') return 0;
        var depth = 0;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (quoted && c == '\\') { index++; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '#' || (c == '/' && index + 1 < value.Length && value[index + 1] == '/'))
            {
                while (index + 1 < value.Length && value[index + 1] != '\n') index++;
                continue;
            }
            if (c == '{') depth++;
            if (c == '}' && --depth == 0) return index + 1;
        }
        return 0;
    }
}
