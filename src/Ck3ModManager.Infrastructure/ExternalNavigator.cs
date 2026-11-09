using static Ck3ModManager.Core.AppText;
using System.Diagnostics;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class ExternalNavigator : IExternalNavigator
{
    private readonly Action<ProcessStartInfo> start;
    private readonly GameDefinition game;
    public ExternalNavigator() : this(GameCatalog.Ck3) { }
    public ExternalNavigator(Action<ProcessStartInfo> processStarter) : this(GameCatalog.Ck3, processStarter) { }
    public ExternalNavigator(GameDefinition game) : this(game, info => Process.Start(info)) { }
    public ExternalNavigator(GameDefinition game, Action<ProcessStartInfo> processStarter)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        start = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    public void LaunchGame() => start(new ProcessStartInfo($"steam://rungameid/{game.AppId}") { UseShellExecute = true });

    public void OpenWorkshop(ulong? publishedFileId = null)
    {
        if (publishedFileId == 0) throw new ArgumentOutOfRangeException(nameof(publishedFileId), T("WorkshopItemIDsMustBeGreaterThan"));
        var uri = publishedFileId is { } id
            ? $"steam://url/CommunityFilePage/{id}"
            : $"steam://openurl/{game.WorkshopHome.AbsoluteUri}";
        start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public void OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains("://", StringComparison.Ordinal)) throw new ArgumentException(T("ChooseALocalFolderToOpen"), nameof(path));
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(T("FolderNotFound", full));
        // A separate ArgumentList entry is used; user paths are never interpreted as shell commands.
        var info = new ProcessStartInfo { UseShellExecute = false };
        if (OperatingSystem.IsWindows()) info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        else if (OperatingSystem.IsMacOS()) info.FileName = "/usr/bin/open";
        else info.FileName = "xdg-open";
        info.ArgumentList.Add(full);
        start(info);
    }
}
