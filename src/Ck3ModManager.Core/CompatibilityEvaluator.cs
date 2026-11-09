using System.Globalization;

namespace Ck3ModManager.Core;

/// <summary>只比较版本声明；不分析脚本、资源覆盖、存档或实际运行兼容性。</summary>
public static class CompatibilityEvaluator
{
    public static CompatibilityResult Evaluate(string? supported, string? actual)
    {
        if (!TryReadVersion(supported, allowWildcard: true, out var declared)
            || !TryReadVersion(actual, allowWildcard: false, out var installed))
            return Unknown("缺少或无法解析游戏版本/Mod 版本声明");

        for (var i = 0; i < declared.Count; i++)
        {
            if (i >= installed.Count) return Unknown("当前游戏版本信息不足以比较完整声明");
            if (declared[i] != installed[i])
                return new(CompatibilityStatus.DeclaredMismatch, "声明不匹配：版本前缀不同；此结果非实测兼容结论。");
        }
        return new(CompatibilityStatus.DeclaredMatch, "声明匹配：游戏版本符合声明的数字前缀/通配符；此结果非实测兼容结论。");
    }

    private static CompatibilityResult Unknown(string reason) =>
        new(CompatibilityStatus.Unknown, $"兼容性未知：{reason}；声明判断，非实测兼容结论。");

    private static bool TryReadVersion(string? text, bool allowWildcard, out List<uint> parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var wildcardSeen = false;
        foreach (var part in text.Trim().Split('.'))
        {
            if (allowWildcard && (part == "*" || part.Equals("x", StringComparison.OrdinalIgnoreCase)))
            {
                wildcardSeen = true;
                continue;
            }
            if (wildcardSeen || part.Length == 0 || !part.All(character => character is >= '0' and <= '9')
                || !uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return false;
            parts.Add(number);
        }
        return parts.Count > 0 || (allowWildcard && wildcardSeen);
    }
}
