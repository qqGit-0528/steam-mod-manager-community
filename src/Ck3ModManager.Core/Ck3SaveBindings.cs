namespace Ck3ModManager.Core;

public sealed record Ck3SaveStamp(long Length, DateTime LastWriteTimeUtc);
public sealed record Ck3SaveMetadata(string? GameVersion, IReadOnlyList<string>? ModDescriptors);
public sealed record Ck3SaveBinding(Guid SnapshotId, Ck3SaveStamp Stamp);
public sealed record Ck3SaveSelection(string Path, Ck3SaveStamp? Stamp, Ck3SaveMetadata Metadata, Ck3SaveBinding? Binding)
{
    public bool NeedsRebind => Binding is not null && Binding.Stamp != Stamp;
}

public interface ICk3SaveBindingStore
{
    Task<Ck3SaveSelection> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task<Ck3SaveSelection> BindAsync(Ck3SaveSelection save, Guid snapshotId, CancellationToken cancellationToken = default);
}

public sealed record Ck3SaveCheck(Ck3SaveSelection Save, IReadOnlyList<string> Differences, bool CanRestore);
