namespace Ck3ModManager.Core;

public sealed record GameDefinition(
    string Id,
    uint AppId,
    string ShortName,
    string DisplayName,
    string GameName,
    string ExecutableName)
{
    public Uri WorkshopHome => new($"https://steamcommunity.com/app/{AppId}/workshop/");
    // 内置游戏都走 Steam 客户端订阅下载；下载后的内容格式由各游戏的扫描器校验。
    public bool SupportsNativeWorkshopDownloads => !IsDiscovered;
    public bool RequiresDeclaredWorkshopCompatibility { get; init; } = true;
    public string LocalModDirectoryName { get; init; } = "mod";
    public bool IsDiscovered { get; init; }
    public bool HasSteamWorkshop { get; init; } = true;
    public string? SteamLibraryHint { get; init; }
    public IReadOnlyList<string> SteamLibraryRoots { get; init; } = [];

    // 强调色取自 Steam 游戏图标的代表色系，并压暗以保证白色按钮文字清晰。
    public string AccentColor { get; init; } = "#F04A3E";

    // 非 Paradox 游戏的用户数据目录不遵循「文档/Paradox Interactive/游戏名」，需要单独指定；~ 表示用户主目录。
    public string? UserDataDirectory { get; init; }

    // 只检查已知程序位置，不递归扫描整个游戏目录。
    public IReadOnlyList<string> ExecutablePaths { get; init; } = Array.AsReadOnly(new[]
    {
        $"binaries/{ExecutableName}.exe", $"binaries/{ExecutableName}",
        $"binaries/{ExecutableName}.app/Contents/MacOS/{ExecutableName}",
        $"binaries/{GameName}.app/Contents/MacOS/{ExecutableName}",
        $"{ExecutableName}.exe", ExecutableName,
        $"{ExecutableName}.app/Contents/MacOS/{ExecutableName}"
    });
}

public static class GameCatalog
{
    public static GameDefinition Ck3 { get; } = new("ck3", 1158310, "CK3", "十字军之王 III", "Crusader Kings III", "ck3") { AccentColor = "#8B2226" };
    public static GameDefinition Hoi4 { get; } = new("hoi4", 394360, "HOI4", "钢铁雄心 IV", "Hearts of Iron IV", "hoi4") { AccentColor = "#716047" };
    public static GameDefinition Eu4 { get; } = new("eu4", 236850, "EU4", "欧陆风云 IV", "Europa Universalis IV", "eu4") { AccentColor = "#83703B" };
    public static GameDefinition Stellaris { get; } = new("stellaris", 281990, "Stellaris", "群星", "Stellaris", "stellaris") { AccentColor = "#366A96" };
    public static GameDefinition Victoria3 { get; } = new("victoria3", 529340, "Victoria 3", "维多利亚 3", "Victoria 3", "victoria3") { AccentColor = "#756448" };
    public static GameDefinition Imperator { get; } = new("imperator", 859580, "Imperator", "英白拉多：罗马", "Imperator", "imperator") { AccentColor = "#A55E31" };

    public static GameDefinition PrisonArchitect { get; } = new("prisonarchitect", 233450, "Prison Architect", "监狱建筑师", "Prison Architect", "Prison Architect")
    {
        AccentColor = "#B65E20",
        RequiresDeclaredWorkshopCompatibility = false,
        LocalModDirectoryName = "mods",
        UserDataDirectory = OperatingSystem.IsMacOS()
            ? "~/Library/Application Support/Prison Architect"
            : OperatingSystem.IsWindows()
                ? "~/AppData/Local/Introversion/Prison Architect"
                : "~/.local/share/Prison Architect",
        // macOS 包名不含空格（PrisonArchitect.app），与默认推导的 Prison Architect.app 不同。
        ExecutablePaths = Array.AsReadOnly(new[]
        {
            "PrisonArchitect.app/Contents/MacOS/PrisonArchitect",
            "Prison Architect.app/Contents/MacOS/Prison Architect",
            "Prison Architect.exe",
            "PrisonArchitect.x86_64",
            "PrisonArchitect"
        })
    };

    public static IReadOnlyList<GameDefinition> All { get; } = Array.AsReadOnly(new[] { Ck3, Hoi4, Eu4, Stellaris, Victoria3, Imperator, PrisonArchitect });

    public static GameDefinition Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(game => string.Equals(game.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"不支持的游戏：{id}", nameof(id));
    }

    public static GameDefinition? FindByAppId(uint appId) => All.FirstOrDefault(game => game.AppId == appId);
}
