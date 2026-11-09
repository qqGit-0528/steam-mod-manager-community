using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class JsonUiLanguagePreference(string storageRoot) : IUiLanguagePreference
{
    private sealed record Preference(string Language);
    private readonly string _path = Path.Combine(Path.GetFullPath(storageRoot), "ui-language.json");

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var value = await JsonFiles.ReadAsync<Preference>(_path, cancellationToken).ConfigureAwait(false);
        return value?.Language is AppText.English or AppText.Chinese ? value.Language : null;
    }

    public Task SaveAsync(string language, CancellationToken cancellationToken = default)
    {
        if (language is not (AppText.English or AppText.Chinese))
            throw new ArgumentOutOfRangeException(nameof(language));
        return JsonFiles.WriteAsync(_path, new Preference(language), cancellationToken);
    }
}
