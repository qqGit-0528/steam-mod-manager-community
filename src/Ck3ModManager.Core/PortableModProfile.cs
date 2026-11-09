using System.Text.Json.Serialization;

namespace Ck3ModManager.Core;

public sealed record PortableModProfile(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] uint GameAppId,
    [property: JsonRequired] string Name,
    string? GameVersion,
    [property: JsonRequired] IReadOnlyList<PortableProfileMod> Mods);

public sealed record PortableProfileMod(
    [property: JsonRequired] string DisplayName,
    ulong? WorkshopId,
    [property: JsonRequired] bool Enabled,
    string? SupportedGameVersion = null,
    string? ModVersion = null);

public enum PortableProfileMatchStatus { Installed, Missing, Ambiguous, VersionMismatch }

public sealed record PortableProfileCandidate(string Id, string Name, string Path, string? SupportedGameVersion,
    string? ModVersion, ModInstallManagement Management);

public sealed record PortableProfileMatch(ProfileMod Mod, PortableProfileMatchStatus Status)
{
    public string? ExpectedVersion { get; init; }
    public IReadOnlyList<PortableProfileCandidate> Candidates { get; init; } = [];
}

public sealed record PortableProfilePreview(ModProfile Profile, IReadOnlyList<PortableProfileMatch> Entries);
