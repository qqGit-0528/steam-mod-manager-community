using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class ProfileDependencyServiceTests
{
    [Fact]
    public async Task ExpandsDiamondOnceAndKeepsCyclesOutOfDownloadPlan()
    {
        var fake = new WorkshopGraph(new Dictionary<ulong, ulong[]?>
        {
            [1] = [2, 3, 5, 7], [2] = [4], [3] = [4], [4] = [], [5] = [6], [6] = [5], [7] = null
        });
        var root = Mod("root", "根", 1);
        var checker = new ProfileDependencyService(new WorkshopService(fake), fake);
        var report = await checker.CheckAsync(Profile(root), [root], TestContext.Current.CancellationToken);
        Assert.Equal(new ulong[] { 4, 2, 3, 7 }, report.MissingWorkshopItems.Select(item => item.PublishedFileId));
        Assert.Equal(1, fake.Lookups.Count(id => id == 4));
        Assert.Contains(report.Findings, row => row.Message == "循环依赖：工坊 5 → 工坊 6 → 工坊 5");
        Assert.Contains(report.Findings, row => row.Message.Contains("工坊 7：工坊依赖暂时无法核对"));
        Assert.DoesNotContain(report.Findings, row => row.Message.Contains("工坊 4：工坊依赖暂时无法核对"));
    }

    [Fact]
    public async Task ResolvesLocalNamesWithoutGuessingBetweenCopiesAndFollowsDisabledDependencies()
    {
        var fake = new WorkshopGraph([]);
        var root = Mod("root", "主 Mod", null, "两个副本", " 关掉的前置 ");
        var disabled = Mod("disabled", "关掉的前置", null, "主 Mod", "找不到的名字");
        var copies = new[] { Mod("one", "两个副本", null), Mod("two", "两个副本", null) };
        var report = await new ProfileDependencyService(new WorkshopService(fake), fake)
            .CheckAsync(Profile(root), [root, disabled, .. copies], TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, row => row.Message.Contains("多个匹配副本"));
        Assert.Contains(report.Findings, row => row.Message.Contains("已安装但未启用"));
        Assert.Contains(report.Findings, row => row.Message.Contains("循环依赖：主 Mod → 关掉的前置 → 主 Mod"));
        Assert.Contains(report.Findings, row => row.Message.Contains("找不到的名字"));
        Assert.Empty(fake.Lookups);
        Assert.Empty(report.MissingWorkshopItems);
    }

    private static ModProfile Profile(InstalledMod root) =>
        new(Guid.NewGuid(), "配置", null, [new(root.Id, root.Name, root.ContentPath, root.WorkshopId, null)], DateTimeOffset.UtcNow);

    private static InstalledMod Mod(string id, string name, ulong? workshop, params string[] dependencies) =>
        new(id, name, "/mods/" + id, "", null, null, workshop, dependencies, ModOrigin.Local, true);

    private sealed class WorkshopGraph(Dictionary<ulong, ulong[]?> nodes) : IWorkshopSource, IWorkshopDependencySource
    {
        public List<ulong> Lookups { get; } = [];
        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default)
        {
            Lookups.Add(publishedFileId);
            return Task.FromResult(new WorkshopItem(publishedFileId, Ck3.AppId, $"工坊 {publishedFileId}", "", null,
                new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}"), DateTimeOffset.UtcNow, 0, [], null));
        }

        public Task<IReadOnlyList<ulong>?> ReadRequiredItemsAsync(uint appId, ulong itemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ulong>?>(nodes.GetValueOrDefault(itemId));
    }
}
