using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Ck3ModManager.Core;
using static Ck3ModManager.Core.AppText;

namespace Ck3ModManager.Desktop;

public sealed class UiLocale : ObservableObject
{
    private const string DictionaryMarker = "Text.UiLanguageDictionary";
    private static readonly SemaphoreSlim PreferenceGate = new(1, 1);

    public static UiLocale Source { get; } = new();
    internal static event Action? LanguageChanged;

    public string Language => AppText.Language;

    internal static void UseLanguage(string? language)
    {
        SetLanguage(language);
        if (Avalonia.Application.Current is { } application) ApplyResources(application);
        Source.Changed(nameof(Language));
        LanguageChanged?.Invoke();
    }

    internal static async Task SaveLanguageAsync(IUiLanguagePreference preference, string language)
    {
        await PreferenceGate.WaitAsync();
        try
        {
            if (language == AppText.Language) return;
            await preference.SaveAsync(language);
            UseLanguage(language);
        }
        finally { PreferenceGate.Release(); }
    }

    internal static void ApplyResources(Avalonia.Application application)
    {
        var resources = new ResourceDictionary { [DictionaryMarker] = true };
        foreach (var key in AppText.Keys) resources["Text." + key] = T(key);

        var merged = application.Resources.MergedDictionaries;
        for (var index = 0; index < merged.Count; index++)
        {
            if (merged[index] is not ResourceDictionary old || !old.ContainsKey(DictionaryMarker)) continue;
            merged[index] = resources;
            return;
        }
        merged.Add(resources);
    }

    internal static string GameName(GameDefinition game) => IsEnglish && !game.IsDiscovered ? game.GameName : game.DisplayName;
}

public sealed class LocalizedGameNameConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.FirstOrDefault() is GameDefinition game ? UiLocale.GameName(game) : "";
}

public sealed partial class MainWindowViewModel
{
    private IUiLanguagePreference? _uiLanguagePreference;
    private bool _savingUiLanguage;

    public bool SupportsUiLanguage => _uiLanguagePreference is not null;
    public bool IsEnglishUi => AppText.IsEnglish;
    public bool IsChineseUi => !IsEnglishUi;
    public ICommand SelectUiLanguageCommand { get; private set; } = null!;

    private void InitializeUiLanguage(IUiLanguagePreference? preference)
    {
        _uiLanguagePreference = preference;
        SelectUiLanguageCommand = new RelayCommand(parameter =>
        {
            if (parameter is string language) _ = SelectUiLanguageAsync(language);
        }, () => SupportsUiLanguage && !_savingUiLanguage && !IsBusy && !_disposed);
        UiLocale.LanguageChanged += RefreshUiLanguage;
    }

    public async Task SelectUiLanguageAsync(string language)
    {
        if (language is not (Chinese or English) || _uiLanguagePreference is null ||
            _savingUiLanguage || IsBusy || _disposed) return;
        _savingUiLanguage = true;
        ((RelayCommand)SelectUiLanguageCommand).Refresh();
        try
        {
            await UiLocale.SaveLanguageAsync(_uiLanguagePreference, language);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!_disposed) ErrorMessage = T("LanguageSaveFailed", error.Message);
        }
        finally
        {
            _savingUiLanguage = false;
            ((RelayCommand)SelectUiLanguageCommand).Refresh();
        }
    }

    private void RefreshUiLanguage()
    {
        if (_disposed) return;
        foreach (var row in InstalledMods) row.RefreshLocalizedText();
        foreach (var row in ProfileMods) row.RefreshLocalizedText();
        foreach (var row in Downloads) row.RefreshLocalizedText();
        RefreshWorkshopLanguage();

        if (_localEnvironment is { } environment) UpdateModDirectories(environment);
        RefreshLocalizedText();
        if (!IsBusy && !WorkshopWorking) Status = T("LanguageChanged");
    }
}
