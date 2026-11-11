using Microsoft.Win32;

namespace Ck3ModManager.Infrastructure;

/// <summary>从 Windows 注册表补全 Steam 安装根目录，覆盖安装在自定义盘符或目录的情况。</summary>
internal static class SteamRootDiscovery
{
    // 32 位 Steam 在 64 位系统上写入 WOW6432Node，64 位 Steam 写入 SOFTWARE\Valve\Steam。
    private static readonly (string Key, string Value)[] WindowsKeys =
    [
        (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    /// <summary>返回注册表声明的 Steam 根目录；非 Windows 或读取失败时返回空集合。</summary>
    internal static IEnumerable<string> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var roots = new List<string>();
        foreach (var (key, value) in WindowsKeys)
        {
            try
            {
                if (Registry.GetValue(key, value, null) is string root && !string.IsNullOrWhiteSpace(root))
                    roots.Add(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        return roots;
    }
}
