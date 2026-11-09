using System.Text;

namespace Ck3ModManager.Infrastructure;

internal sealed record TextProperty(string Name, string? Scalar, IReadOnlyList<string>? Block);

/// <summary>Valve VDF 与 Paradox descriptor 的小型只读词法器；不执行脚本。</summary>
internal static class TextDataParser
{
    public static IReadOnlyList<TextProperty> Read(string text) => ReadTokens(Tokenize(text));

    public static IReadOnlyList<TextProperty> ReadTokens(IReadOnlyList<string> tokens)
    {
        var result = new List<TextProperty>();
        var index = 0;
        while (index < tokens.Count)
        {
            var name = tokens[index++];
            if (name is "=" or "{" or "}") throw new FormatException("属性名或括号结构不正确。");
            if (index < tokens.Count && tokens[index] == "=") index++;
            if (index == tokens.Count) throw new FormatException($"属性 {name} 缺少值。");
            if (tokens[index] == "{")
            {
                var start = ++index;
                var depth = 1;
                while (index < tokens.Count && depth > 0)
                {
                    if (tokens[index] == "{") depth++;
                    if (tokens[index] == "}") depth--;
                    index++;
                }
                if (depth != 0) throw new FormatException($"属性 {name} 的大括号未闭合。");
                result.Add(new(name, null, tokens.Skip(start).Take(index - start - 1).ToArray()));
            }
            else
            {
                if (tokens[index] is "}" or "=") throw new FormatException($"属性 {name} 缺少值。");
                result.Add(new(name, tokens[index++], null));
            }
        }
        return result;
    }

    public static string? Value(this IReadOnlyList<TextProperty> properties, string name) =>
        properties.LastOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Scalar;

    public static string ReadSmallFile(string path, long maxBytes = 1_048_576)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", path);
        if (info.Length > maxBytes) throw new InvalidDataException("描述文件过大，已跳过。");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
            if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}' or '=') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                i++;
                var value = new StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    c = text[i++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\' && i < text.Length && (text[i] == '\\' || text[i] == '"')) c = text[i++];
                    value.Append(c);
                }
                if (!closed) throw new FormatException("引号没有闭合。");
                tokens.Add(value.ToString());
                continue;
            }
            var begin = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}' and not '=' and not '#') i++;
            if (begin == i) throw new FormatException("无法识别描述文件中的字符。");
            tokens.Add(text[begin..i]);
        }
        return tokens;
    }
}
