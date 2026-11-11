using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Ck3ModManager.Desktop.Tests;

public sealed class FontEmbeddingTests
{
    [AvaloniaFact]
    public void AppFontFamilyResolvesAndContainsChineseGlyphs()
    {
        var resource = global::Avalonia.Application.Current!.FindResource("AppFontFamily");
        var family = Assert.IsType<FontFamily>(resource);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphTypeface));
        Assert.Equal("Noto Sans SC", glyphTypeface.FamilyName);
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('中', out var glyph));
        Assert.NotEqual(0, glyph);
    }
}
