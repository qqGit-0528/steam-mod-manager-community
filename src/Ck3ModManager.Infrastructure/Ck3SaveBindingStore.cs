using static Ck3ModManager.Core.AppText;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class Ck3SaveBindingStore(string storageRoot) : ICk3SaveBindingStore
{
    private readonly string _root = Path.Combine(Path.GetFullPath(storageRoot), "save-bindings");

    public async Task<Ck3SaveSelection> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".ck3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(T("SelectALocalCk3SaveFile"), nameof(path));

        var full = Path.GetFullPath(path);
        var binding = await JsonFiles.ReadAsync<Ck3SaveBinding>(BindingPath(full), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Inspect(full, binding, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Ck3SaveSelection> BindAsync(Ck3SaveSelection save, Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(snapshotId, Guid.Empty);
        var current = await ReadAsync(save.Path, cancellationToken).ConfigureAwait(false);
        if (current.Stamp is null || current.Stamp != save.Stamp)
            throw new InvalidOperationException(T("TheSaveMovedOrChangedSelectIt"));
        var binding = new Ck3SaveBinding(snapshotId, current.Stamp);
        await JsonFiles.WriteAsync(BindingPath(current.Path), binding, cancellationToken).ConfigureAwait(false);
        return current with { Binding = binding };
    }

    private static Ck3SaveSelection Inspect(string path, Ck3SaveBinding? binding, CancellationToken token)
    {
        var stamp = Stamp(path);
        if (stamp is null) return new(path, null, new(null, null), binding);
        Ck3SaveMetadata metadata;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            metadata = Ck3SaveMetadataReader.Read(stream, token);
        }
        catch (FileNotFoundException) { return new(path, null, new(null, null), binding); }
        catch (DirectoryNotFoundException) { return new(path, null, new(null, null), binding); }
        var after = Stamp(path);
        if (stamp != after) throw new IOException(T("TheSaveChangedWhileBeingReadSelect"));
        return new(path, stamp, metadata, binding);
    }

    private static Ck3SaveStamp? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(info.Length, info.LastWriteTimeUtc) : null;
    }

    private string BindingPath(string path)
    {
        var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return Path.Combine(_root, SnapshotFiles.TextHash(identity) + ".json");
    }
}
