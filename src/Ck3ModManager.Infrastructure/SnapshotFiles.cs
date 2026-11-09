using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ck3ModManager.Infrastructure;

internal static class SnapshotFiles
{
    internal static string Fingerprint(string source, string? destination, CancellationToken token)
    {
        if (destination is not null && (SteamGameLocator.PathComparer.Equals(source, destination) ||
            SteamGameLocator.IsWithin(source, destination)))
            throw new InvalidDataException("Mod 目录包含快照存储位置。");

        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var nodes = new Stack<(string Source, string? Destination, string Relative)>();
        nodes.Push((source, destination, ""));
        Span<byte> header = stackalloc byte[5];

        while (nodes.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(node.Source);
            RejectLink(node.Source, attributes);
            var directory = (attributes & FileAttributes.Directory) != 0;
            var name = Encoding.UTF8.GetBytes(node.Relative);
            header[0] = directory ? (byte)1 : (byte)2;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], name.Length);
            tree.AppendData(header);
            tree.AppendData(name);

            if (directory)
            {
                if (node.Destination is not null) Directory.CreateDirectory(node.Destination);
                foreach (var child in Directory.GetFileSystemEntries(node.Source).OrderDescending(StringComparer.Ordinal))
                {
                    var childName = Path.GetFileName(child);
                    var relative = node.Relative.Length == 0 ? childName : node.Relative + "/" + childName;
                    nodes.Push((child, node.Destination is null ? null : Path.Combine(node.Destination, childName), relative));
                }
                continue;
            }

            using var input = new FileStream(node.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cloned = node.Destination is not null && TryClone(node.Source, node.Destination);
            using var output = node.Destination is null || cloned ? null :
                new FileStream(node.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                file.AppendData(buffer.AsSpan(0, read));
                output?.Write(buffer, 0, read);
            }
            tree.AppendData(file.GetHashAndReset());
        }

        return Convert.ToHexString(tree.GetHashAndReset());
    }

    internal static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // APFS 写时复制：快照与源共享物理块，修改任一侧时才分配新块。
    private static bool TryClone(string source, string destination)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return CloneFile(source, destination, 0) == 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, int flags);

    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException($"快照不支持符号链接：{path}");
    }

    private static void RejectLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"快照不支持符号链接：{path}");
    }
}
