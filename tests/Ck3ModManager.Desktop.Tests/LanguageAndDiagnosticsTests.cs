using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ck3ModManager.Application;
using Ck3ModManager.Core;
using Ck3ModManager.Desktop;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop.Tests;

[CollectionDefinition("Desktop locale", DisableParallelization = true)]
public sealed class DesktopLocaleCollection;

[Collection("Desktop locale")]
public sealed class LanguageAndDiagnosticsTests
{
    [AvaloniaFact]
    public async Task SwitchingToEnglishRefreshesTheWindowAndKeepsTheDraft()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var token = TestContext.Current.CancellationToken;
        Assert.Null(await fixture.Preference.LoadAsync(token));
        await vm.InitializeAsync(token);
        vm.CreateProfile();
        vm.ProfileName = "草稿名不翻译";
        vm.SelectedWorkshopSort = vm.WorkshopSorts[1];
        vm.Section = "settings";
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 690 };
        try
        {
            window.Show();
            Layout(window);
            Assert.Equal("zh-CN", AppText.Language);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.IsEffectivelyVisible && block.Text == "界面语言");

            await vm.SelectUiLanguageAsync(AppText.English);
            Layout(window);
            Assert.Equal("en", await new JsonUiLanguagePreference(fixture.Root).LoadAsync(token));
            Assert.Equal("Crusader Kings III · Settings", vm.SectionTitle);
            Assert.Equal("Most subscribed", vm.SelectedWorkshopSort!.Label);
            Assert.Equal("All categories", vm.WorkshopCategoryInput);
            Assert.Equal("草稿名不翻译", vm.ProfileName);
            Assert.True(vm.IsProfileDirty);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.IsEffectivelyVisible && block.Text == "Interface language");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), block => block.IsEffectivelyVisible && block.Text == "界面语言");
            Save(window, "english-settings.png");

            vm.Section = "profiles";
            Layout(window);
            Assert.Equal("草稿名不翻译", vm.ProfileName);
            Save(window, "english-profiles.png");
            vm.Section = "diagnostics";
            await vm.ReadDiagnosticsAsync();
            Assert.Equal("CK3 crashes and logs", vm.SectionTitle);
            Assert.Equal("This log is empty.", vm.DiagnosticLogTail);
            Layout(window);
            Save(window, "english-diagnostics.png");

            using var other = fixture.Create(GameCatalog.Victoria3);
            Assert.Equal("Victoria 3 · Local mods", other.LocalLibraryTitle);
            Assert.False(other.SupportsCk3Diagnostics);
        }
        finally
        {
            if (AppText.IsEnglish) await vm.SelectUiLanguageAsync(AppText.Chinese);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UnknownPreferencesFallBackAndFailedSavesKeepTheCurrentLanguage()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var path = Path.Combine(fixture.Root, "ui-language.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, """{"language":"unknown"}""", token);
        Assert.Null(await fixture.Preference.LoadAsync(token));

        File.Delete(path);
        Directory.CreateDirectory(path);
        await vm.SelectUiLanguageAsync(AppText.English);
        Assert.Equal(AppText.Chinese, AppText.Language);
        Assert.Contains("无法保存界面语言", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task ReadsAndOpensTheSelectedGroupFromTheDiagnosticsPage()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var report = Path.Combine(fixture.Environment.UserDataDirectory, "crashes", "ck3_test_report");
        Directory.CreateDirectory(report);
        File.WriteAllText(Path.Combine(report, "exception.txt"), "raw exception details");
        vm.Section = "diagnostics";
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 800 };
        try
        {
            window.Show(); Layout(window);
            var read = window.FindControl<Button>("ReadDiagnosticsButton")!;
            Assert.True(read.IsEffectivelyVisible && read.IsEffectivelyEnabled);
            await vm.ReadDiagnosticsAsync();
            Assert.True(vm.HasDiagnosticsReport);
            Assert.Equal("此日志为空。", vm.DiagnosticLogTail);
            var crash = Assert.Single(vm.DiagnosticGroups, group => group.Group.ReportName is not null);
            vm.SelectedDiagnosticGroup = crash;
            Assert.Equal("raw exception details", vm.DiagnosticLogTail);
            Assert.Equal("crashes/ck3_test_report/exception.txt", vm.SelectedDiagnosticLog!.RelativePath);
            vm.OpenDiagnosticDirectoryCommand.Execute(null);
            Assert.Equal(report, fixture.Opened);
            Layout(window);
            Save(window, "chinese-diagnostics.png");

            vm.Section = "settings";
            Assert.False(vm.HasDiagnosticsReport);
            Assert.Empty(vm.DiagnosticGroups);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task WorkspaceSwitchDiscardsAnInFlightDiagnosticsResult()
    {
        using var fixture = new Fixture();
        var reader = new DelayedReader();
        using var vm = fixture.Create(reader: reader);
        vm.Section = "diagnostics";
        var operation = vm.ReadDiagnosticsAsync();
        var token = TestContext.Current.CancellationToken;
        await reader.Started.Task.WaitAsync(token);

        vm.SetLocalModMonitoringEnabled(false);
        Assert.True(reader.Token.IsCancellationRequested);
        reader.Result.SetResult(new(fixture.Root, null, [], []));
        await operation.WaitAsync(token);
        Assert.False(vm.HasDiagnosticsReport);
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CK3_LANGUAGE_SCREENSHOTS") is { Length: > 0 } directory)
            window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class DelayedReader : ICk3DiagnosticsReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Ck3DiagnosticsReport> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<Ck3DiagnosticsReport> ReadAsync(GameEnvironment environment, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            return Result.Task;
        }
    }

    private sealed class Fixture : IGameLocator, IWorkshopSource, IExternalNavigator, IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ck3-language-ui-").FullName;
        public GameEnvironment Environment => new(Path.Combine(Root, "install"), Path.Combine(Root, "user"), "1.20.0.4", [], []);
        public JsonUiLanguagePreference Preference => new(Root);
        public string? Opened { get; private set; }

        public Fixture()
        {
            var logs = Path.Combine(Environment.UserDataDirectory, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "error.log"), "");
        }

        public MainWindowViewModel Create(GameDefinition? game = null, ICk3DiagnosticsReader? reader = null)
        {
            var definition = game ?? GameCatalog.Ck3;
            var workspace = new WorkspaceService(this, new Ck3ModScanner(), new JsonProfileStore(Root), new JsonSettingsStore(Root),
                diagnostics: definition.AppId == Ck3.AppId ? reader ?? new Ck3DiagnosticsReader() : null);
            return new(workspace, new WorkshopService(this, game: definition), this, uiLanguage: Preference);
        }

        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Environment);

        public Task<WorkshopItem> GetItemAsync(ulong publishedFileId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected network lookup");

        public void LaunchGame() { }
        public void OpenWorkshop(ulong? publishedFileId = null) { }
        public void OpenDirectory(string path) => Opened = path;
        public void Dispose() => Directory.Delete(Root, true);
    }
}
