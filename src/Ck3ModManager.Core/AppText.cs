using System.Globalization;
using System.Resources;

namespace Ck3ModManager.Core;

public static class AppText
{
    public const string Chinese = "zh-CN";
    public const string English = "en";
    private static readonly ResourceManager Resources = new("Ck3ModManager.Core.Resources.Messages", typeof(AppText).Assembly);
    private static CultureInfo _language = CultureInfo.InvariantCulture;

    public static string Language => Volatile.Read(ref _language).Name == English ? English : Chinese;
    public static bool IsEnglish => Language == English;

    public static void SetLanguage(string? language) =>
        Volatile.Write(ref _language, language == English ? CultureInfo.GetCultureInfo(English) : CultureInfo.InvariantCulture);

    public static string T(string key, params object?[] arguments)
    {
        var language = Volatile.Read(ref _language);
        var template = Resources.GetString(key, language) ?? key;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    public static IEnumerable<string> Keys => Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
        .Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key);
}
