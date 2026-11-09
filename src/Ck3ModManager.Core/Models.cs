namespace Ck3ModManager.Core;

public static class Ck3
{
    public const uint AppId = 1158310;
    public const string GameName = "Crusader Kings III";
    public static readonly Uri WorkshopHome = new("https://steamcommunity.com/app/1158310/workshop/");
}

public enum ModOrigin { Local, SteamWorkshop }
public enum ModInstallManagement { External, Local, Steam, Application }
public enum CompatibilityStatus { Unknown, DeclaredMatch, DeclaredMismatch }

public sealed record CompatibilityResult(CompatibilityStatus Status, string Message);

public sealed record WorkshopItem(
    ulong PublishedFileId,
    uint ConsumerAppId,
    string Title,
    string Description,
    string? PreviewImageUrl,
    Uri WorkshopUrl,
    DateTimeOffset UpdatedAt,
    long FileSizeBytes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ulong>? RequiredItemIds,
    long? Subscriptions = null,
    string? CreatorId = null);

public enum WorkshopSort { Trending, MostSubscribed, MostRecent, RecentlyUpdated }

public sealed record WorkshopSearchRequest(
    string SearchText = "", WorkshopSort Sort = WorkshopSort.Trending, int Page = 1,
    string? CategoryTag = null, string? VersionTag = null);

public sealed record WorkshopBrowsePage(
    IReadOnlyList<WorkshopItem> Items, int Page, int TotalPages, int TotalCount,
    IReadOnlyList<string> CategoryTags, IReadOnlyList<string> VersionTags);

public sealed record InstalledMod(
    string Id,
    string Name,
    string ContentPath,
    string DescriptorPath,
    string? ModVersion,
    string? SupportedGameVersion,
    ulong? WorkshopId,
    IReadOnlyList<string> Dependencies,
    ModOrigin Origin,
    bool IsAvailable)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ModInstallManagement Management { get; init; }
}

public enum LocalInstallationState { Unknown, Missing, Installed }
public sealed record LocalInstallation(LocalInstallationState State, string? OpenDirectory = null);

public sealed record ModScanResult(IReadOnlyList<InstalledMod> Mods, IReadOnlyList<string> Warnings)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public GameEnvironment? Environment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long ScanStartedAt { get; init; }
}

public sealed record DiscoveryOptions(
    string? SteamDirectory = null,
    string? GameDirectory = null,
    string? UserDataDirectory = null);

public sealed record GameEnvironment(
    string? GameDirectory,
    string UserDataDirectory,
    string? GameVersion,
    IReadOnlyList<string> WorkshopDirectories,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> LocalModDirectories { get; init; } = [Path.Combine(UserDataDirectory, "mod")];
    public IReadOnlyList<string> LocalModWatchPaths { get; init; } = [];
}

public sealed record ProfileMod(
    string InstalledModId,
    string DisplayName,
    string ContentPath,
    ulong? WorkshopId,
    string? SupportedGameVersion,
    string? ModVersion = null)
{
    public bool Enabled { get; init; } = true;
}

public sealed record ModProfile(
    Guid Id,
    string Name,
    string? GameVersion,
    IReadOnlyList<ProfileMod> Mods,
    DateTimeOffset UpdatedAt);

public sealed record AppSettings(
    string? SteamDirectory = null,
    string? GameDirectory = null,
    string? UserDataDirectory = null)
{
    public DiscoveryOptions ToDiscoveryOptions() => new(SteamDirectory, GameDirectory, UserDataDirectory);
}
