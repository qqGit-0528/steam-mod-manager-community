using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class GameCatalogTests
{
    [Theory]
    [InlineData("ck3", 1158310U)]
    [InlineData("hoi4", 394360U)]
    [InlineData("eu4", 236850U)]
    [InlineData("stellaris", 281990U)]
    [InlineData("victoria3", 529340U)]
    [InlineData("imperator", 859580U)]
    [InlineData("prisonarchitect", 233450U)]
    public void SupportedGamesHaveDistinctSteamIdentities(string id, uint appId)
    {
        var game = GameCatalog.Get(id);
        Assert.Equal(appId, game.AppId);
        Assert.Same(game, GameCatalog.FindByAppId(appId));
        Assert.Same(game, Assert.Single(GameCatalog.All, candidate => candidate.Id == id));
    }

    [Fact]
    public void UnknownGameCannotSilentlyFallBackToCk3()
    {
        Assert.Throws<ArgumentException>(() => GameCatalog.Get("unknown-game"));
        Assert.Null(GameCatalog.FindByAppId(570));
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(game => game.Id).Distinct().Count());
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(game => game.AppId).Distinct().Count());
    }
}
