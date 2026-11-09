using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ck3ModManager.Core;

public sealed record WorkshopProject(string Key, string Title, IReadOnlyList<WorkshopItem> Releases);

public sealed record WorkshopReleaseChoice(
    WorkshopItem Item,
    IReadOnlyList<string> GameVersions,
    CompatibilityStatus Compatibility,
    string Recommendation,
    string? ModVersion);

/// <summary>
/// 组织实际工坊条目，并根据作者声明提供选择顺序；不生成历史文件或实测兼容结论。
/// </summary>
public static class WorkshopCatalog
{
    private const string NumericVersion = @"[0-9]+\.[0-9]+(?:\.[0-9]+)*";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex VersionTag = new(
        "^\\s*(?<version>" + NumericVersion + ")" +
        "(?:\\s+(?:'[^'\\r\\n]+'|\"[^\"\\r\\n]+\"|‘[^’\\r\\n]+’|“[^”\\r\\n]+”))?\\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly IReadOnlyDictionary<uint, Regex> GameTitleSuffixes = GameCatalog.All.ToDictionary(
        game => game.AppId, game => new Regex(
            @"^(?<title>.+?)(?:\s*\[\s*(?:" + GameTitleMarker(game) + @")\s+(?<version>" + NumericVersion + @")\s*\]|" +
            @"\s*\(\s*(?:" + GameTitleMarker(game) + @")\s+(?<version>" + NumericVersion + @")\s*\)|" +
            @"\s+-\s+(?:" + GameTitleMarker(game) + @")\s+(?<version>" + NumericVersion + @"))\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout));
    private static readonly Regex GameVersionPrefix = new(
        @"(?:^|\s|\[|\()(?:" + string.Join("|", GameCatalog.All.Select(GameTitleMarker)) + @")\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex TaggedVersionTitleSuffix = new(
        @"^(?<title>.+?)(?:\s*\[\s*(?<version>" + NumericVersion + @")\s*\]|" +
        @"\s*\(\s*(?<version>" + NumericVersion + @")\s*\)|" +
        @"\s+-\s+(?<version>" + NumericVersion + @"))\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ExplicitModVersion = new(
        @"(?<![\p{L}\p{N}_])(?<version>[vV][0-9]+(?:\.[0-9]+)+(?:[-+][0-9A-Za-z][0-9A-Za-z.-]*)?)(?![\p{L}\p{N}_.])",
        RegexOptions.CultureInvariant, RegexTimeout);

    public static IReadOnlyList<WorkshopProject> Group(IEnumerable<WorkshopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // 同一 ID 只保留一份真实条目；重复响应发生冲突时采用更新时间较新的元数据。
        var unique = DistinctItems(items);
        return unique.GroupBy(GroupKey, StringComparer.Ordinal)
            .Select(group => new WorkshopProject(group.Key, ReadTitle(group.First()).Title, group.ToArray()))
            .ToArray();
    }

    public static IReadOnlyList<WorkshopReleaseChoice> Rank(WorkshopProject project, string? actualGameVersion)
    {
        ArgumentNullException.ThrowIfNull(project);
        return DistinctItems(project.Releases)
            .Select(item => Evaluate(item, actualGameVersion))
            .OrderBy(choice => choice.Compatibility switch
            {
                CompatibilityStatus.DeclaredMatch => 0,
                CompatibilityStatus.Unknown => 1,
                _ => 2
            })
            .ThenByDescending(choice => choice.Item.UpdatedAt)
            .ToArray();
    }

    public static WorkshopReleaseChoice EvaluateItem(WorkshopItem item, string? actualGameVersion)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Evaluate(item, actualGameVersion);
    }

    public static CompatibilityStatus EvaluateGameVersions(IReadOnlyList<string> versions, string? actualGameVersion)
    {
        if (versions.Count == 0 || CompatibilityEvaluator.Evaluate("*", actualGameVersion).Status != CompatibilityStatus.DeclaredMatch)
            return CompatibilityStatus.Unknown;
        var allMismatch = true;
        foreach (var version in versions)
        {
            var status = CompatibilityEvaluator.Evaluate(version, actualGameVersion).Status;
            if (status == CompatibilityStatus.DeclaredMatch) return status;
            allMismatch &= status == CompatibilityStatus.DeclaredMismatch;
        }
        return allMismatch ? CompatibilityStatus.DeclaredMismatch : CompatibilityStatus.Unknown;
    }

    private static WorkshopReleaseChoice Evaluate(WorkshopItem item, string? actualGameVersion)
    {
        var versions = ReadGameVersionTags(item);
        var evidence = "作者标签声明";
        var title = ReadTitle(item);
        if (versions.Length == 0 && title.GameVersion is not null)
        {
            versions = [title.GameVersion];
            evidence = "标题中的游戏版本声明（无可识别版本标签时的备用信息）";
        }

        var status = EvaluateGameVersions(versions, actualGameVersion);
        string recommendation;
        if (CompatibilityEvaluator.Evaluate("*", actualGameVersion).Status != CompatibilityStatus.DeclaredMatch)
            recommendation = "未检测到可比较的目标游戏版本，暂不推荐适配度；版本标签/标题声明均非实测兼容结论。";
        else if (versions.Length == 0)
            recommendation = "兼容性未知：没有可识别的游戏版本标签或明确的当前游戏标题声明；非实测兼容结论。";
        else
            recommendation = status switch
            {
                CompatibilityStatus.DeclaredMatch => $"优先查看：{evidence}符合目标游戏 {actualGameVersion}；非实测兼容结论。",
                CompatibilityStatus.DeclaredMismatch => $"{evidence}与目标游戏 {actualGameVersion}不匹配；非实测兼容结论。",
                _ => $"兼容性未知：{evidence}无法与目标游戏版本完整比较；非实测兼容结论。"
            };

        var modVersions = ExplicitModVersion.Matches(title.Title)
            .Where(match => !GameVersionPrefix.IsMatch(title.Title[..match.Index]))
            .Take(2).ToArray();
        return new WorkshopReleaseChoice(item, versions, status, recommendation,
            modVersions.Length == 1 ? modVersions[0].Groups["version"].Value : null);
    }

    private static IEnumerable<WorkshopItem> DistinctItems(IEnumerable<WorkshopItem> items) =>
        items.GroupBy(item => (item.ConsumerAppId, item.PublishedFileId)).Select(group => group.OrderByDescending(item => item.UpdatedAt).First());

    private static string GroupKey(WorkshopItem item)
    {
        var creator = item.CreatorId?.Trim();
        var title = ReadTitle(item).Title;
        if (string.IsNullOrEmpty(creator) || creator == "0" || title.Length == 0)
            return $"workshop/{item.ConsumerAppId.ToString(CultureInfo.InvariantCulture)}/{item.PublishedFileId.ToString(CultureInfo.InvariantCulture)}";
        // URI 编码避免标题或作者身份包含分隔符时发生键碰撞。
        return $"workshop/{item.ConsumerAppId.ToString(CultureInfo.InvariantCulture)}/creator/{Uri.EscapeDataString(creator)}/title/{Uri.EscapeDataString(title.ToUpperInvariant())}";
    }

    private static string[] ReadGameVersionTags(WorkshopItem item) => item.Tags.Select(tag => VersionTag.Match(tag))
        .Where(match => match.Success).Select(match => match.Groups["version"].Value)
        .Distinct(StringComparer.Ordinal).ToArray();

    private static (string Title, string? GameVersion) ReadTitle(WorkshopItem item)
    {
        var normalized = NormalizeWhitespace(item.Title.Normalize(NormalizationForm.FormC));
        var suffix = GameTitleSuffixes.TryGetValue(item.ConsumerAppId, out var gameSuffix)
            ? gameSuffix.Match(normalized) : Match.Empty;
        if (!suffix.Success)
        {
            suffix = TaggedVersionTitleSuffix.Match(normalized);
            // 普通数字尾标只有被当前条目自己的明确游戏标签支持时才可剥离。
            if (!suffix.Success || !ReadGameVersionTags(item).Contains(suffix.Groups["version"].Value, StringComparer.Ordinal))
                return (normalized, null);
        }
        var name = suffix.Groups["title"].Value.Trim();
        return name.Length == 0 ? (normalized, null) : (name, suffix.Groups["version"].Value);
    }

    private static string GameTitleMarker(GameDefinition game)
    {
        var names = new List<string> { game.ShortName, game.GameName };
        if (game.AppId == GameCatalog.Victoria3.AppId) names.AddRange(["VIC3", "V3"]);
        if (game.AppId == GameCatalog.Imperator.AppId) names.AddRange(["Imperator: Rome", "I:R"]);
        return string.Join("|", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
    }

    private static string NormalizeWhitespace(string text)
    {
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) { pendingSpace = output.Length > 0; continue; }
            if (pendingSpace) { output.Append(' '); pendingSpace = false; }
            output.Append(character);
        }
        return output.ToString();
    }
}
