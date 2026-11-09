using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public static class GameStorage
{
    public static string GetDirectory(string root, GameDefinition game)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(game.Id) || game.Id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("游戏 ID 只能包含英文字母、数字、连字符和下划线。", nameof(game));
        var fullRoot = Path.GetFullPath(root);
        return game.Id == GameCatalog.Ck3.Id && game.AppId == GameCatalog.Ck3.AppId
            ? fullRoot
            : Path.Combine(fullRoot, "games", game.Id);
    }
}
