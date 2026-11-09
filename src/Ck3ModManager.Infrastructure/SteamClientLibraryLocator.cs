using static Ck3ModManager.Core.AppText;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal static class SteamClientLibraryLocator
{
    internal static string Find(GameDefinition game, string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException(T("TheGameInstallationWasNotFoundConfirm"));
        var root = Path.GetFullPath(gameDirectory);
        var name = OperatingSystem.IsWindows() ? "steam_api64.dll" : OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : "libsteam_api.so";
        var relative = new List<string>();
        foreach (var executable in game.ExecutablePaths)
        {
            var directory = Path.GetDirectoryName(executable);
            if (directory is not null) relative.Add(Path.Combine(directory, name));
            var marker = executable.IndexOf(".app/Contents/", StringComparison.Ordinal);
            if (marker >= 0) relative.Add(Path.Combine(executable[..(marker + ".app/Contents".Length)], "Frameworks", name));
        }
        // 可执行程序实际使用的库优先，game 目录可能残留旧架构的 SDK。
        relative.AddRange([name, Path.Combine("binaries", name), Path.Combine("game", name)]);
        string? incompatible = null;
        foreach (var candidate in relative.Distinct(StringComparer.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(root, candidate));
            var within = Path.GetRelativePath(root, path);
            if (within == ".." || within.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(within)) continue;
            if (!File.Exists(path)) continue;
            if (SupportsProcessArchitecture(path)) return path;
            incompatible ??= path;
        }
        if (incompatible is null)
            throw new FileNotFoundException(T("TheGameSSteamClientLibraryWas"));
        // 监狱建筑师等旧游戏只提供 Intel 版接口库；Apple Silicon 上借用本机其他游戏同架构的 Steamworks 运行时。
        if (OperatingSystem.IsMacOS())
        {
            if (FindCompatibleSteamApi(game) is { } fallback) return fallback;
            throw new PlatformNotSupportedException(T("TheGameSSteamLibraryIsIntel"));
        }
        return incompatible;
    }

    /// <summary>库文件是否包含当前进程可加载的 Mach-O 架构。</summary>
    internal static bool SupportsProcessArchitecture(string path)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        var target = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => 0x0100000Cu,
            Architecture.X64 => 0x01000007u,
            _ => (uint?)null
        };
        if (target is null) return true;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic == 0xCAFEBABE)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                Span<byte> architecture = stackalloc byte[20];
                for (var index = 0; index < count; index++)
                {
                    stream.ReadExactly(architecture);
                    if (BinaryPrimitives.ReadUInt32BigEndian(architecture) == target) return true;
                }
                return false;
            }
            // 单架构 64 位 Mach-O：0xFEEDFACF 在磁盘上以小端存储。
            if (magic == 0xCFFAEDFE)
                return BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == target;
            return true;
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return true; }
    }

    private static string? FindCompatibleSteamApi(GameDefinition game)
    {
        var name = "libsteam_api.dylib";
        IReadOnlyList<string> libraries;
        try
        {
            var warnings = new List<string>();
            libraries = new SteamGameLocator(game).FindLibraries(null, warnings, CancellationToken.None).Libraries;
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return null; }

        foreach (var library in libraries)
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var installed in GameCatalog.All)
            {
                foreach (var executable in installed.ExecutablePaths)
                {
                    var marker = executable.IndexOf(".app/Contents/", StringComparison.Ordinal);
                    if (marker < 0) continue;
                    var candidate = Path.GetFullPath(Path.Combine(common, installed.GameName,
                        executable[..(marker + ".app/Contents".Length)], "Frameworks", name));
                    if (File.Exists(candidate) && SupportsProcessArchitecture(candidate)) return candidate;
                }
            }
        }
        foreach (var library in libraries)
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
                foreach (var candidate in Directory.EnumerateFiles(common, name, options).Take(128))
                    if (SupportsProcessArchitecture(candidate)) return candidate;
            }
            catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { }
        }
        return null;
    }
}
