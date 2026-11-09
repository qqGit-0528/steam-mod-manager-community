namespace Ck3ModManager.Core;

public interface IUiLanguagePreference
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string language, CancellationToken cancellationToken = default);
}
