using static Ck3ModManager.Core.AppText;
using System.Text.Json;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal static class JsonFiles
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(AppText.T("SettingsFileIsEmptyBackItUp", path));
        }
        catch (JsonException ex)
        { throw new InvalidDataException(AppText.T("SettingsFileIsCorruptOrUsesAn", path), ex); }
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    public JsonProfileStore(string rootDirectory) => path = Path.Combine(Path.GetFullPath(rootDirectory), "profiles.json");

    public async Task<IReadOnlyList<ModProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadProfilesAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read before writing so damaged JSON is never silently replaced.
            var profiles = await ReadProfilesAsync(cancellationToken).ConfigureAwait(false);
            var index = profiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0) profiles[index] = profile;
            else profiles.Add(profile);
            await JsonFiles.WriteAsync(path, profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 未落盘的配置不在文件里，删除时保持文件原样。
            var profiles = await ReadProfilesAsync(cancellationToken).ConfigureAwait(false);
            if (profiles.RemoveAll(p => p.Id == id) == 0) return;
            await JsonFiles.WriteAsync(path, profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<List<ModProfile>> ReadProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await JsonFiles.ReadAsync<List<ModProfile>>(path, cancellationToken).ConfigureAwait(false) ?? [];
        try
        {
            foreach (var profile in profiles) Validate(profile);
            if (profiles.Select(p => p.Id).Distinct().Count() != profiles.Count) throw new ArgumentException(T("DuplicatePlaysetIDsWereFound"));
        }
        catch (ArgumentException ex) { throw new InvalidDataException(T("PlaysetFileIsInvalidAndWasPreserved", path, ex.Message), ex); }
        return profiles;
    }

    private static void Validate(ModProfile profile)
    {
        if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Mods is null)
            throw new ArgumentException(T("ThePlaysetIsMissingItsIDName"));
        foreach (var mod in profile.Mods)
            if (mod is null || string.IsNullOrWhiteSpace(mod.InstalledModId) || mod.ContentPath is null || mod.DisplayName is null)
                throw new ArgumentException(T("ThePlaysetContainsInvalidModInformation"));
    }
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    public JsonSettingsStore(string rootDirectory) => path = Path.Combine(Path.GetFullPath(rootDirectory), "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await JsonFiles.ReadAsync<AppSettings>(path, cancellationToken).ConfigureAwait(false) ?? new AppSettings(); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await JsonFiles.ReadAsync<AppSettings>(path, cancellationToken).ConfigureAwait(false);
            await JsonFiles.WriteAsync(path, settings, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
