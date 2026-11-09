using Avalonia;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => SteamClientWorker.IsWorker(args)
        ? SteamClientWorker.Run(args) : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
