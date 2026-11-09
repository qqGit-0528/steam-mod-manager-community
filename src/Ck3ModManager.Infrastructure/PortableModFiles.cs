using static Ck3ModManager.Core.AppText;
using System.IO.Compression;
using System.Text;

namespace Ck3ModManager.Infrastructure;

internal static class PortableModFiles
{
    internal sealed record Entry(string Path, long? Length, Func<Stream>? Open);
    internal const long MaxBytes = 64L * 1024 * 1024 * 1024;
    internal const int MaxEntries = 500_000;
    private const int MaxDepth = 48;

    internal static IReadOnlyList<Entry> ReadZip(ZipArchive archive, CancellationToken token)
    {
        IEnumerable<Entry> Entries()
        {
            foreach (var item in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var attributes = item.ExternalAttributes;
                var type = (attributes >> 16) & 0xf000;
                if (type is not (0 or 0x4000 or 0x8000) || (attributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(T("TheZIPContainsALinkOrSpecial"));
                var path = item.FullName.Replace('\\', '/');
                var directory = path.EndsWith('/') || type == 0x4000 || (attributes & (int)FileAttributes.Directory) != 0;
                if (directory && item.Length != 0) throw new InvalidDataException(T("AZIPDirectoryContainsFileData"));
                if (path.EndsWith('/')) path = path[..^1];
                yield return directory ? new(path, null, null) : new(path, item.Length, item.Open);
            }
        }
        return Validate(Entries(), token);
    }

    internal static IReadOnlyList<Entry> ReadDirectory(string root, CancellationToken token)
    {
        EnsureOrdinary(root);
        IEnumerable<Entry> Entries()
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                EnsureOrdinary(directory);
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = EnsureOrdinary(child);
                    var relative = Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/');
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                        yield return new(relative, null, null);
                    }
                    else yield return new(relative, new FileInfo(child).Length, () => OpenWithin(root, child));
                }
            }
        }
        return Validate(Entries(), token);
    }

    private static IReadOnlyList<Entry> Validate(IEnumerable<Entry> entries, CancellationToken token)
    {
        var output = new List<Entry>();
        var names = new Dictionary<string, (string Exact, bool Directory, bool Explicit)>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var parts = entry.Path.Split('/');
            if (entry.Path.Length > 4096 || parts.Length > MaxDepth || parts.Any(part => !IsPortableName(part)))
                throw new InvalidDataException(T("UnsupportedPathInThePackage", entry.Path));
            var path = "";
            for (var index = 0; index < parts.Length; index++)
            {
                path = index == 0 ? parts[0] : path + "/" + parts[index];
                var explicitEntry = index == parts.Length - 1;
                var directory = !explicitEntry || entry.Length is null;
                var key = path.Normalize(NormalizationForm.FormC);
                if (names.TryGetValue(key, out var prior) &&
                    (prior.Exact != path || prior.Directory != directory || prior.Explicit && explicitEntry))
                    throw new InvalidDataException(T("DuplicateOrCaseConflictingPackagePath", entry.Path));
                names[key] = (path, directory, explicitEntry || prior.Explicit);
                if (names.Count > MaxEntries) throw new InvalidDataException(T("ThePackageContainsMoreThan500000"));
            }

            if (entry.Length is { } length)
            {
                if (length < 0 || length > MaxBytes - bytes) throw new InvalidDataException(T("TheUnpackedPackageExceeds64GiB"));
                bytes += length;
            }
            output.Add(entry);
        }
        return output;
    }

    private static bool IsPortableName(string name)
    {
        if (name.Length is 0 or > 255 || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(character => char.IsControl(character) || "<>:\"|?*\\/".Contains(character))) return false;
        var stem = name.Split('.', 2)[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
              (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    internal static void Extract(IEnumerable<Entry> entries, string root, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Length is null) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                Copy(entry, target, buffer, token);
            }
        }
    }

    internal static void Copy(Entry entry, Stream target, byte[] buffer, CancellationToken token)
    {
        using var source = entry.Open!();
        var remaining = entry.Length!.Value;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException(T("FileLengthChanged", entry.Path));
            target.Write(buffer, 0, read);
            remaining -= read;
        }
        if (source.ReadByte() != -1) throw new InvalidDataException(T("FileExceedsItsDeclaredLength", entry.Path));
    }

    internal static string ReadText(Entry entry, CancellationToken token)
    {
        if (entry.Length is null or > 1_048_576) throw new InvalidDataException(T("InvalidDescriptorOrLargerThan1MiB", entry.Path));
        using var data = new MemoryStream((int)entry.Length.Value);
        Copy(entry, data, new byte[8192], token);
        data.Position = 0;
        using var reader = new StreamReader(data, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static FileStream OpenWithin(string root, string path)
    {
        for (var parent = Path.GetDirectoryName(path)!; ; parent = Path.GetDirectoryName(parent)!)
        {
            EnsureOrdinary(parent);
            if (SteamGameLocator.PathComparer.Equals(parent, root)) break;
        }
        return OpenRead(path);
    }

    internal static FileStream OpenRead(string path)
    {
        EnsureOrdinary(path);
        return File.OpenRead(path);
    }

    internal static FileAttributes EnsureOrdinary(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException(T("LinkOrSpecialFileIsUnsupported", path));
        return attributes;
    }

    internal static void DeleteStage(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
