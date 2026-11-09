using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public static class PortableProfileService
{
    private const int CurrentVersion = 1;
    private const int MaxBytes = 1024 * 1024;
    private const int MaxMods = 1000;
    private const int MaxNameLength = 512;
    private const int MaxVersionLength = 128;
    private const string UnresolvedPrefix = "portable:";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static async Task WriteAsync(Stream destination, GameDefinition game, ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = new PortableModProfile(CurrentVersion, game.AppId, profile.Name, profile.GameVersion,
            profile.Mods.Select(mod => new PortableProfileMod(mod.DisplayName, mod.WorkshopId,
                mod.Enabled, mod.SupportedGameVersion, mod.ModVersion)).ToArray());
        Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException(T("TheProfileExceeds1MiBAndCannot"));
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PortableModProfile> ReadAsync(Stream source, GameDefinition game,
        CancellationToken cancellationToken = default)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                    MaxBytes + 1 - (int)data.Length)), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > MaxBytes)
                throw new InvalidDataException(T("TheProfileExceeds1MiB"));
            data.Write(buffer, 0, count);
        }

        PortableModProfile? document;
        try
        {
            document = Deserialize(data);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(T("InvalidProfileFileFormat"), error);
        }

        if (document is null)
            throw new InvalidDataException(T("TheProfileFileIsEmpty"));
        Validate(document);
        if (document.GameAppId != game.AppId)
            throw new InvalidDataException(T("ThisProfileBelongsToAnotherGameSwitch"));
        return document;
    }

    private static PortableModProfile? Deserialize(MemoryStream data)
    {
        var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        return JsonSerializer.Deserialize<PortableModProfile>(bytes, Json);
    }

    public static PortableProfilePreview Match(PortableModProfile document, IReadOnlyList<InstalledMod> installed,
        IReadOnlyDictionary<int, PortableProfileCandidate>? choices = null)
    {
        Validate(document);
        var entries = document.Mods.Select((entry, index) =>
        {
            var id = entry.WorkshopId is { } workshopId
                ? UnresolvedPrefix + "steam:" + workshopId.ToString(CultureInfo.InvariantCulture)
                : UnresolvedPrefix + "local:" + index.ToString(CultureInfo.InvariantCulture);
            var mod = new ProfileMod(id, entry.DisplayName, "", entry.WorkshopId, entry.SupportedGameVersion,
                entry.ModVersion) { Enabled = entry.Enabled };
            return MatchEntry(mod, installed, choices?.GetValueOrDefault(index));
        }).ToArray();

        var profile = new ModProfile(Guid.NewGuid(), document.Name.Trim(), document.GameVersion,
            Array.AsReadOnly(entries.Select(entry => entry.Mod).ToArray()), DateTimeOffset.UtcNow);
        return new(profile, Array.AsReadOnly(entries));
    }

    public static bool IsUnresolved(ProfileMod mod) =>
        mod.InstalledModId.StartsWith(UnresolvedPrefix, StringComparison.Ordinal) && mod.ContentPath.Length == 0;

    public static ProfileMod ResolveMissing(ProfileMod mod, IReadOnlyList<InstalledMod> installed) =>
        IsUnresolved(mod) ? MatchEntry(mod, installed).Mod : mod;

    public static bool MatchesInstallation(ProfileMod mod, InstalledMod candidate) =>
        candidate.IsAvailable && (mod.WorkshopId is { } id ? candidate.WorkshopId == id :
            candidate.WorkshopId is null && candidate.Origin == ModOrigin.Local &&
            StringComparer.OrdinalIgnoreCase.Equals(candidate.Name.Trim(), mod.DisplayName.Trim()));

    public static ProfileMod UseInstalledCopy(ProfileMod mod, InstalledMod candidate) =>
        IsUnresolved(mod) && MatchesInstallation(mod, candidate) ? UseCandidate(mod, ToCandidate(candidate)) : mod;

    private static PortableProfileCandidate ToCandidate(InstalledMod candidate) =>
        new(candidate.Id, candidate.Name, candidate.ContentPath, candidate.SupportedGameVersion, candidate.ModVersion, candidate.Management);

    private static ProfileMod UseCandidate(ProfileMod mod, PortableProfileCandidate candidate) => mod with
    {
        InstalledModId = candidate.Id,
        DisplayName = candidate.Name,
        ContentPath = candidate.Path,
        SupportedGameVersion = candidate.SupportedGameVersion,
        ModVersion = candidate.ModVersion
    };

    private static PortableProfileMatch MatchEntry(ProfileMod mod, IReadOnlyList<InstalledMod> installed,
        PortableProfileCandidate? choice = null)
    {
        var candidates = installed.Where(candidate => MatchesInstallation(mod, candidate))
            .DistinctBy(candidate => candidate.Id).Select(ToCandidate).ToArray();
        var hasVersion = !string.IsNullOrWhiteSpace(mod.ModVersion);
        var suitable = hasVersion
            ? candidates.Where(candidate => StringComparer.Ordinal.Equals(candidate.ModVersion?.Trim(), mod.ModVersion!.Trim())).ToArray()
            : candidates;
        var selected = choice is not null && candidates.Contains(choice) ? choice :
            suitable.Length == 1 ? suitable[0] : null;
        var status = selected is not null ? PortableProfileMatchStatus.Installed : candidates.Length == 0
            ? PortableProfileMatchStatus.Missing : suitable.Length == 0
            ? PortableProfileMatchStatus.VersionMismatch : PortableProfileMatchStatus.Ambiguous;
        return new(selected is null ? mod : UseCandidate(mod, selected), status)
        {
            ExpectedVersion = mod.ModVersion,
            Candidates = candidates
        };
    }

    private static void Validate(PortableModProfile document)
    {
        if (document.FormatVersion != CurrentVersion)
            throw new InvalidDataException(T("ThisProfileFileVersionIsUnsupported"));
        if (document.GameAppId == 0 || !ValidText(document.Name, MaxNameLength) || document.Mods is null)
            throw new InvalidDataException(T("TheProfileFileIsMissingAValid"));
        if (document.Mods.Count > MaxMods)
            throw new InvalidDataException(T("AProfileCanContainUpTo1"));
        if (!ValidOptionalText(document.GameVersion, MaxVersionLength))
            throw new InvalidDataException(T("InvalidGameVersionField"));

        var workshopIds = new HashSet<ulong>();
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in document.Mods)
        {
            if (mod is null || !ValidText(mod.DisplayName, MaxNameLength) || mod.WorkshopId == 0
                || !ValidOptionalText(mod.SupportedGameVersion, MaxVersionLength)
                || !ValidOptionalText(mod.ModVersion, MaxVersionLength))
                throw new InvalidDataException(T("TheProfileContainsInvalidModInformation"));
            if (mod.WorkshopId is { } id ? !workshopIds.Add(id) : !localNames.Add(mod.DisplayName.Trim()))
                throw new InvalidDataException(T("TheProfileContainsIndistinguishableDuplicateMods", mod.DisplayName));
        }
    }

    private static bool ValidText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool ValidOptionalText(string? value, int maxLength) =>
        value is null || value.Length <= maxLength && !value.Any(char.IsControl);
}
