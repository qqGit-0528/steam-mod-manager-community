using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;

namespace Ck3ModManager.Desktop.Tests;

public sealed class CompatibilityDisplayTests
{
    [Theory]
    [InlineData("1.20.*", "兼容", true, false)]
    [InlineData("1.19.*", "不兼容", false, true)]
    [InlineData(null, "未知", false, false)]
    public void LocalCompatibilityUsesShortLabelsAndPreservesUnknown(string? supported, string text, bool matched, bool mismatched)
    {
        var row = new InstalledModRow(Mod("mod", supported), "1.20.3");
        Assert.Equal(text, row.Compatibility);
        Assert.Equal(matched, row.IsCompatibilityMatched);
        Assert.Equal(mismatched, row.IsCompatibilityMismatch);
    }

    [Theory]
    [InlineData("1.20", "兼容")]
    [InlineData("1.19", "不兼容")]
    [InlineData(null, "未知")]
    public void WorkshopRowsShowOnlyShortCompatibilityText(string? tag, string expected)
    {
        var item = new WorkshopItem(101, Ck3.AppId, "Mod", "", null, new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=101"),
            DateTimeOffset.UtcNow, 1, tag is null ? [] : [tag], null);
        using var row = new WorkshopModRow(Assert.Single(WorkshopCatalog.Group([item])), "1.20.3", new WorkshopService(new Ports()), _ => Task.CompletedTask);
        Assert.Equal(expected, row.Recommendation);
    }

    [AvaloniaFact]
    public async Task LocalLabelsAreRenderedInGreenAndRed()
    {
        var ports = new Ports();
        using var vm = new MainWindowViewModel(new WorkspaceService(ports, ports, ports, ports), new WorkshopService(ports), ports);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var labels = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var good = Assert.Single(labels.Where(label => label.Text == "兼容"));
            var bad = Assert.Single(labels.Where(label => label.Text == "不兼容"));
            Assert.Equal(Color.Parse("#2F9E6E"), Assert.IsAssignableFrom<ISolidColorBrush>(good.Foreground).Color);
            Assert.Equal(Color.Parse("#D9453A"), Assert.IsAssignableFrom<ISolidColorBrush>(bad.Foreground).Color);
            var screenshots = Environment.GetEnvironmentVariable("CK3_DOWNLOAD_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(screenshots))
            {
                Directory.CreateDirectory(screenshots);
                window.CaptureRenderedFrame()!.Save(Path.Combine(screenshots, "compatibility-short-labels.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    private static InstalledMod Mod(string id, string? supported) => new(id, id, "test/" + id, "test/" + id + ".mod", "1", supported, null, [], ModOrigin.Local, true);
    private sealed class Ports : IGameLocator, IModScanner, IProfileStore, ISettingsStore, IWorkshopSource, IExternalNavigator
    {
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new GameEnvironment(null, "test-user", "1.20.3", [], []));
        public Task<ModScanResult> ScanAsync(string directory, IReadOnlyList<string> workshopDirectories, CancellationToken cancellationToken = default) => Task.FromResult(new ModScanResult([Mod("兼容版本", "1.20.*"), Mod("旧版本", "1.19.*")], []));
        Task<IReadOnlyList<ModProfile>> IProfileStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModProfile>>([]);
        Task<AppSettings> ISettingsStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(ModProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkshopItem> GetItemAsync(ulong id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不应查询网络。");
        public void OpenDirectory(string path) { }
        public void OpenWorkshop(ulong? id = null) { }
        public void LaunchGame() { }
    }
}
