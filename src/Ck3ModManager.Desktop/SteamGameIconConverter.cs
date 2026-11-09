using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

/// <summary>按需读取随应用打包的游戏徽标，复用每款游戏的解码结果。</summary>
public sealed class SteamGameIconConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, Lazy<Bitmap>> Icons = GameCatalog.All.ToDictionary(
        game => game.Id,
        game => new Lazy<Bitmap>(() =>
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Ck3ModManager.Desktop/Assets/Games/{game.Id}.png"));
            return new Bitmap(stream);
        }));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GameDefinition game && Icons.TryGetValue(game.Id, out var icon) ? icon.Value : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
