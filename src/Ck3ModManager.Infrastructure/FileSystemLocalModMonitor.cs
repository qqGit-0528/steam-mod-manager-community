using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>初始扫描后只响应本地文件系统事件，不定时查询或扫描。</summary>
public sealed class FileSystemLocalModMonitor : ILocalModMonitor
{
    private readonly IGameLocator _gameLocator;
    private readonly IModScanner _modScanner;
    private readonly TimeSpan _debounce;

    public FileSystemLocalModMonitor(IGameLocator gameLocator, IModScanner modScanner, TimeSpan? debounce = null)
    {
        _gameLocator = gameLocator ?? throw new ArgumentNullException(nameof(gameLocator));
        _modScanner = modScanner ?? throw new ArgumentNullException(nameof(modScanner));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public async IAsyncEnumerable<ModScanResult> WatchAsync(DiscoveryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
        });
        using var watches = new LocalWatches(changes.Writer);
        var fallback = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.UserDataDirectory)) fallback.Add(Path.Combine(options.UserDataDirectory, "mod"));
        if (!string.IsNullOrWhiteSpace(options.SteamDirectory)) fallback.Add(Path.Combine(options.SteamDirectory, "steamapps", "libraryfolders.vdf"));
        watches.Configure(fallback);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModScanResult? snapshot = null;
            try
            {
                var started = Stopwatch.GetTimestamp();
                var environment = await _gameLocator.DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                watches.Configure(environment.LocalModWatchPaths
                    .Concat(environment.LocalModDirectories).Concat(environment.WorkshopDirectories));
                snapshot = (await _modScanner.ScanAsync(environment, cancellationToken).ConfigureAwait(false))
                    with { Environment = environment, ScanStartedAt = started };
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 文件可能正被 Steam 替换；保留上次快照，等下一个实际事件再检查。
            }

            if (snapshot is not null) yield return snapshot;

            await changes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            while (changes.Reader.TryRead(out _)) { }
        }
    }

    private sealed class LocalWatches(ChannelWriter<bool> changes) : IDisposable
    {
        private sealed record Target(string Path, bool IsFile);
        private sealed record Watch(FileSystemWatcher Watcher, bool Recursive);
        private readonly Dictionary<string, Watch> _watches = new(SteamGameLocator.PathComparer);
        private Target[] _targets = [];
        private int _reset;

        public void Configure(IEnumerable<string> paths)
        {
            var targets = new List<Target>();
            foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(SteamGameLocator.PathComparer))
            {
                try
                {
                    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    targets.Add(new Target(full, File.Exists(full) ||
                        string.Equals(Path.GetFileName(full), "libraryfolders.vdf", StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception error) when (IsWatchFailure(error)) { }
            }
            Volatile.Write(ref _targets, targets.ToArray());
            var desired = new Dictionary<string, bool>(SteamGameLocator.PathComparer);
            foreach (var target in targets)
            {
                if (!target.IsFile && Directory.Exists(target.Path)) desired[target.Path] = true;
                var parent = Path.GetDirectoryName(target.Path);
                while (parent is not null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
                // 不存在目标只监测最近存在的父目录；不递归观察整个用户目录。
                if (parent is not null && !desired.ContainsKey(parent)) desired[parent] = false;
            }

            if (Interlocked.Exchange(ref _reset, 0) != 0) DisposeWatches();
            foreach (var (path, recursive) in desired)
            {
                if (_watches.TryGetValue(path, out var existing))
                {
                    if (existing.Recursive == recursive) continue;
                    existing.Watcher.Dispose();
                    _watches.Remove(path);
                }
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    watcher.Created += OnStructure;
                    watcher.Deleted += OnStructure;
                    watcher.Renamed += OnRenamed;
                    watcher.Changed += OnChanged;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;
                    _watches.Add(path, new Watch(watcher, recursive));
                }
                catch (Exception error) when (IsWatchFailure(error)) { watcher?.Dispose(); }
            }
            foreach (var path in _watches.Keys.Where(path => !desired.ContainsKey(path)).ToArray())
            {
                _watches[path].Watcher.Dispose();
                _watches.Remove(path);
            }
        }

        private void OnStructure(object sender, FileSystemEventArgs change)
        {
            if (Relevant(change.FullPath)) changes.TryWrite(true);
        }

        private void OnRenamed(object sender, RenamedEventArgs change)
        {
            if (Relevant(change.FullPath) || Relevant(change.OldFullPath)) changes.TryWrite(true);
        }

        private void OnChanged(object sender, FileSystemEventArgs change)
        {
            foreach (var target in Volatile.Read(ref _targets))
            {
                if (target.IsFile && SamePath(target.Path, change.FullPath) ||
                    !target.IsFile && Inside(target.Path, change.FullPath) &&
                    (string.Equals(Path.GetFileName(change.FullPath), "descriptor.mod", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetExtension(change.FullPath), ".mod", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetFileName(change.FullPath), "metadata.json", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetFileName(change.FullPath), "manifest.txt", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(change.FullPath).EndsWith("_legacy.bin", StringComparison.OrdinalIgnoreCase)))
                {
                    changes.TryWrite(true);
                    return;
                }
            }
        }

        private bool Relevant(string path) => Volatile.Read(ref _targets).Any(target =>
            SamePath(target.Path, path) || Inside(path, target.Path) || !target.IsFile && Inside(target.Path, path));

        private void OnError(object sender, ErrorEventArgs change)
        {
            Interlocked.Exchange(ref _reset, 1);
            changes.TryWrite(true);
        }

        private static bool SamePath(string first, string second) => SteamGameLocator.PathComparer.Equals(first, second);
        private static bool Inside(string parent, string child) => child.StartsWith(
            Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        private static bool IsWatchFailure(Exception error) => error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException;
        private void DisposeWatches()
        {
            foreach (var watch in _watches.Values) watch.Watcher.Dispose();
            _watches.Clear();
        }
        public void Dispose() => DisposeWatches();
    }
}
