using static Ck3ModManager.Core.AppText;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

internal sealed record SteamClientRequest(uint AppId, ulong ItemId, string LibraryPath, string WorkingDirectory)
{
    internal IReadOnlyList<ulong>? StatusItems { get; init; }
    internal bool Unsubscribe { get; init; }
}

public sealed class SteamClientWorkshopDownloader : ISteamClientWorkshopDownloader, IWorkshopStatusSource
{
    private readonly GameDefinition _game;
    private readonly IGameLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly string _root;
    private readonly Func<SteamClientRequest, CancellationToken, IAsyncEnumerable<SteamClientFrame>> _transport;

    public SteamClientWorkshopDownloader(GameDefinition game, IGameLocator locator, ISettingsStore settings, string storageRoot)
        : this(game, locator, settings, storageRoot, ReadProcessAsync) { }

    internal SteamClientWorkshopDownloader(GameDefinition game, IGameLocator locator, ISettingsStore settings, string storageRoot,
        Func<SteamClientRequest, CancellationToken, IAsyncEnumerable<SteamClientFrame>> transport)
    { _game = game; _locator = locator; _settings = settings; _root = Path.GetFullPath(storageRoot); _transport = transport; }

    public async Task<SteamClientInstallation> DownloadAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
    {
        if (!_game.SupportsNativeWorkshopDownloads) throw new NotSupportedException(T("ThisGameSSteamLibraryDoesNot"));
        if (item.ConsumerAppId != _game.AppId || item.PublishedFileId == 0) throw new InvalidDataException(T("TheDownloadItemDoesNotMatchThe"));
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _locator.DiscoverAsync(settings.ToDiscoveryOptions(), cancellationToken).ConfigureAwait(false);
        var request = CreateRequest(environment, item.PublishedFileId);
        SteamClientFrame? complete = null;
        await foreach (var frame in _transport(request, cancellationToken).ConfigureAwait(false))
        {
            if (frame.AppId != item.ConsumerAppId || frame.ItemId != item.PublishedFileId)
                throw new InvalidDataException(T("TheSteamBridgeReturnedADifferentGame"));
            if (frame.Bytes < 0 || frame.TotalBytes < 0) throw new InvalidDataException(T("SteamReturnedInvalidDownloadByteCounts"));
            if (complete is not null) throw new InvalidDataException(T("TheSteamBridgeReturnedAnUnexpectedMessage"));
            switch (frame.Type)
            {
                case "connected": progress.Report(new(WorkshopDownloadState.Resolving, Message: T("ConnectedToTheLocalSteamClient"))); break;
                case "subscribing": progress.Report(new(WorkshopDownloadState.Resolving, Message: T("SubscribingToTheModInSteam"))); break;
                case "submitted": progress.Report(new(WorkshopDownloadState.Queued, Message: T("QueuedInSteam2"))); break;
                case "progress":
                    var downloading = (frame.State & 16) != 0;
                    progress.Report(new(downloading ? WorkshopDownloadState.Downloading : WorkshopDownloadState.Queued,
                        frame.Bytes, frame.TotalBytes, downloading ? T("DownloadingInSteam") : T("WaitingForSteamToDownload"),
                        TransferredBytes: frame.Bytes));
                    break;
                case "complete": complete = frame; break;
                case "error": throw new IOException(frame.Error ?? T("SteamDidNotFinishTheDownload"));
                default: throw new InvalidDataException(T("TheSteamBridgeReturnedAnUnsupportedMessage"));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (complete?.ContentPath is not { Length: > 0 } path || !Path.IsPathFullyQualified(path) ||
            (!Directory.Exists(path) && !File.Exists(path)) || complete.SizeOnDisk is not >= 0)
            throw new IOException(T("SteamDidNotConfirmThatInstallationFinished"));
        if (!IsRecognizableContent(path, item.PublishedFileId))
            throw new InvalidDataException(_game.Id == GameCatalog.PrisonArchitect.Id
                ? T("SteamReturnedEmptyOrUnreadableContentVerify")
                : T("SteamReturnedContentWithoutAReadableMod"));
        return new SteamClientInstallation(path, complete.SizeOnDisk.Value, complete.Reused, complete.Bytes, complete.TotalBytes,
            TryReadRevision(path, item.ConsumerAppId, item.PublishedFileId));
    }

    public async Task UnsubscribeAsync(GameEnvironment environment, ulong itemId, CancellationToken token)
    {
        if (!_game.SupportsNativeWorkshopDownloads || itemId == 0)
            throw new InvalidOperationException(T("UseTheSteamClientToUnsubscribeAnd"));
        var request = CreateRequest(environment, itemId) with { Unsubscribe = true };
        var connected = false;
        var confirmed = false;
        await foreach (var frame in _transport(request, token).ConfigureAwait(false))
        {
            if (frame.AppId != request.AppId || frame.ItemId != itemId || confirmed)
                throw new InvalidDataException(T("TheSteamUnsubscribeResponseDoesNotMatch"));
            if (frame.Type == "error") throw new IOException(frame.Error ?? T("SteamFailedToUnsubscribe"));
            if (!connected && frame.Type == "connected") connected = true;
            else if (connected && frame.Type == "unsubscribed") confirmed = true;
            else throw new InvalidDataException(T("TheSteamUnsubscribeResponseIsInvalid"));
        }
        if (!confirmed) throw new IOException(T("SteamDidNotConfirmTheUnsubscribe"));
    }

    public async Task<WorkshopStatusCheck> CheckAsync(GameEnvironment environment, IReadOnlyList<ulong> itemIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = itemIds.Distinct().ToHashSet();
        var states = new Dictionary<ulong, WorkshopUpdateState>();
        if (ids.Count == 0) return new(states);
        if (!_game.SupportsNativeWorkshopDownloads || ids.Contains(0))
            throw new ArgumentException(T("StatusChecksRequireAValidWorkshopID"), nameof(itemIds));
        if (ids.Count > SteamClientWorker.MaxStatusItems)
            throw new InvalidOperationException(T("CheckUpToWorkshopModsAtA", SteamClientWorker.MaxStatusItems));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var request = CreateRequest(environment, 0) with { StatusItems = ids.ToArray() };
            var connected = false;
            await foreach (var frame in _transport(request, deadline.Token).ConfigureAwait(false))
            {
                if (frame.AppId != _game.AppId) throw new InvalidDataException(T("TheSteamBridgeReturnedStatusForAnother"));
                if (frame.Type == "error" && frame.ItemId == 0)
                    throw new IOException(frame.Error ?? T("CouldNotReadSteamStatus"));
                if (!connected && frame.Type == "connected" && frame.ItemId == 0)
                {
                    connected = true;
                    continue;
                }
                if (!connected || frame.Type != "state" || !ids.Contains(frame.ItemId) ||
                    !states.TryAdd(frame.ItemId, Classify(frame.State)))
                    throw new InvalidDataException(T("TheSteamBridgeReturnedAnInvalidStatus"));
            }
            return new(states, states.Count == ids.Count ? null : T("SteamOmittedTheStatusOfSomeItems"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(states, T("TheSteamStatusRequestTimedOut")); }
        catch (InvalidDataException error) { return new(new Dictionary<ulong, WorkshopUpdateState>(), error.Message); }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(states, error.Message); }
    }

    private static WorkshopUpdateState Classify(uint flags)
    {
        const uint running = SteamClientDownloadLoop.Downloading | SteamClientDownloadLoop.Pending;
        if ((flags & running) != 0) return WorkshopUpdateState.Processing;
        if ((flags & SteamClientDownloadLoop.Installed) == 0) return WorkshopUpdateState.Unknown;
        if ((flags & SteamClientDownloadLoop.NeedsUpdate) != 0) return WorkshopUpdateState.NeedsUpdate;
        return (flags & SteamClientDownloadLoop.Subscribed) != 0
            ? WorkshopUpdateState.NoPendingUpdate : WorkshopUpdateState.Unknown;
    }

    private bool IsRecognizableContent(string path, ulong itemId) =>
        _game.Id == GameCatalog.PrisonArchitect.Id
            // 旧版工坊内容由 Steam 保存为 UGC 容器（可能是目录，也可能直接是 legacy 文件），由游戏自己读取。
            ? File.Exists(path) ? new FileInfo(path).Length > 0
                : Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()
            : Ck3ModScanner.IsRecognizableWorkshopContent(path, itemId);

    private SteamClientRequest CreateRequest(GameEnvironment environment, ulong itemId)
    {
        var library = SteamClientLibraryLocator.Find(_game, environment.GameDirectory);
        var work = Path.Combine(_root, "steam-client", _game.AppId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);
        return new(_game.AppId, itemId, library, work);
    }

    internal static string? TryReadRevision(string path, uint appId, ulong itemId)
    {
        try
        {
            var item = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
            if (item.Name != itemId.ToString() || item.Parent?.Name != appId.ToString() || item.Parent.Parent?.Name != "content") return null;
            var index = Path.Combine(item.Parent.Parent.Parent!.FullName, $"appworkshop_{appId}.acf");
            var root = TextDataParser.Read(TextDataParser.ReadSmallFile(index, 5_242_880));
            var app = root.FirstOrDefault(field => field.Name == "AppWorkshop")?.Block;
            if (app is null) return null;
            var installed = TextDataParser.ReadTokens(app).FirstOrDefault(field => field.Name == "WorkshopItemsInstalled")?.Block;
            if (installed is null) return null;
            var entry = TextDataParser.ReadTokens(installed).FirstOrDefault(field => field.Name == itemId.ToString())?.Block;
            var manifest = entry is null ? null : TextDataParser.ReadTokens(entry).Value("manifest");
            return ulong.TryParse(manifest, out var id) && id != 0 ? "steam-client:" + manifest : null;
        }
        catch (Exception error) when (SteamGameLocator.IsReadableError(error)) { return null; }
    }

    internal static ProcessStartInfo CreateStartInfo(SteamClientRequest request)
    {
        var executable = Environment.ProcessPath ?? throw new IOException(T("CouldNotLocateTheDownloadBridgeExecutable"));
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true, WorkingDirectory = request.WorkingDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new IOException(T("CouldNotLocateTheApplicationEntryAssembly")));
        info.ArgumentList.Add(request.Unsubscribe ? SteamClientWorker.UnsubscribeCommand :
            request.StatusItems is not null ? SteamClientWorker.StatusCommand : SteamClientWorker.Command);
        info.ArgumentList.Add(request.AppId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(request.StatusItems is { } items
            ? string.Join(",", items.Select(item => item.ToString(CultureInfo.InvariantCulture)))
            : request.ItemId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(request.LibraryPath);
        info.Environment["SteamAppId"] = request.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["SteamGameId"] = request.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment[SteamClientWorker.LanguageVariable] = AppText.Language;
        return info;
    }

    private static IAsyncEnumerable<SteamClientFrame> ReadProcessAsync(SteamClientRequest request, CancellationToken token) =>
        ReadProcessFramesAsync(CreateStartInfo(request), TimeSpan.FromMinutes(1), token);

    internal static async IAsyncEnumerable<SteamClientFrame> ReadProcessFramesAsync(ProcessStartInfo startInfo,
        TimeSpan responseTimeout, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new IOException(T("CouldNotStartTheSteamClientBridge"));
        // 原生 SDK 可能向 stderr 写账户诊断；只排空，不向界面、任务历史或日志转发。
        using var drain = new CancellationTokenSource();
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, drain.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var connected = false;
        try
        {
            while (true)
            {
                string? line;
                try { line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException(connected ? T("TheSteamDownloadBridgeStoppedRespondingTracking")
                    : T("TheSteamConnectionTimedOutCheckThat")); }
                if (line is null) break;
                var frame = SteamClientFrame.Decode(line);
                if (frame is null) continue;
                if (frame.Type == "connected") connected = true;
                if (connected) deadline.CancelAfter(responseTimeout);
                yield return frame;
            }
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException(T("TheSteamBridgeTimedOutWhileExiting")); }
            if (process.ExitCode != 0) throw new IOException(T("TheSteamDownloadBridgeExitedUnexpectedlyCode", process.ExitCode));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false); }
                catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // 只结束本工具的桥接进程，不关闭或暂停 Steam。
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            await drain.CancelAsync().ConfigureAwait(false);
            try { await errors.ConfigureAwait(false); }
            catch (OperationCanceledException) when (drain.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
}
