using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamRootDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ck3-steam-root-{Guid.NewGuid():N}");

    [Fact]
    public void RequiresSteamAppsDirectory()
    {
        Directory.CreateDirectory(root);
        Assert.False(SteamGameLocator.LooksLikeSteamRoot(root));

        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Assert.True(SteamGameLocator.LooksLikeSteamRoot(root));
    }

    [Fact]
    public void FindLibrariesIgnoresConfiguredRootsWithoutSteamApps()
    {
        var fake = Path.Combine(root, "fake");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(fake);
        Directory.CreateDirectory(Path.Combine(real, "steamapps"));

        var game = new GameDefinition("test-game", 900001, "Test", "Test", "Test", "Test") { IsDiscovered = true };
        var locator = new SteamGameLocator(game, [fake, real]);
        var (libraries, knownLibraries, knownRoots) = locator.FindLibraries(null, [], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { real }, libraries);
        Assert.DoesNotContain(fake, knownLibraries);
        Assert.DoesNotContain(fake, knownRoots);
    }

    [Fact]
    public void RegistryRootsAreEmptyOutsideWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Empty(SteamRootDiscovery.Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
