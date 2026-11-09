using Avalonia;
using Avalonia.Media;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

/// <summary>切换游戏时替换应用级强调色资源，界面通过 DynamicResource 跟随变化。</summary>
public static class GameTheme
{
    private static readonly Color Fallback = Color.Parse("#F04A3E");

    public static void Apply(GameDefinition game)
    {
        if (Avalonia.Application.Current is not { } application) return;
        var accent = Color.TryParse(game.AccentColor, out var parsed) ? parsed : Fallback;
        var resources = application.Resources;
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentHoverBrush"] = new SolidColorBrush(Shade(accent, 0.87));
        resources["AccentPressedBrush"] = new SolidColorBrush(Shade(accent, 0.74));
        resources["AccentSoftBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.92));
        resources["AccentSoftHoverBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.85));
        resources["AccentLineBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.72));
    }

    private static Color Shade(Color color, double factor) => Color.FromArgb(color.A,
        (byte)Math.Round(color.R * factor), (byte)Math.Round(color.G * factor), (byte)Math.Round(color.B * factor));

    private static Color Mix(Color color, Color other, double amount) => Color.FromArgb(color.A,
        (byte)Math.Round(color.R + (other.R - color.R) * amount),
        (byte)Math.Round(color.G + (other.G - color.G) * amount),
        (byte)Math.Round(color.B + (other.B - color.B) * amount));
}
