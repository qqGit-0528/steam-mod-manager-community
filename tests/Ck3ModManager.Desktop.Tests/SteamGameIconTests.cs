using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class SteamGameIconTests
{
    [AvaloniaFact]
    public void EveryGameHasAnEmbeddedBadgeAndSharesTheDecodedImage()
    {
        var converter = new SteamGameIconConverter();
        foreach (var game in GameCatalog.All)
        {
            var image = Assert.IsType<Bitmap>(converter.Convert(game, typeof(Bitmap), null, CultureInfo.InvariantCulture));
            Assert.Equal(32, image.PixelSize.Width);
            Assert.Equal(32, image.PixelSize.Height);
            Assert.Same(image, new SteamGameIconConverter().Convert(game, typeof(Bitmap), null, CultureInfo.InvariantCulture));
        }
        Assert.Null(converter.Convert(null, typeof(Bitmap), null, CultureInfo.InvariantCulture));
    }
}
