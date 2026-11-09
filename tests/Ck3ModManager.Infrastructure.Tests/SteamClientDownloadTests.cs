using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ck3ModManager.Core;
using Ck3ModManager.Infrastructure;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class SteamClientDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("steam-client-test-").FullName;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CleaningOldDuplicatesCannotDeleteARecordUpdatedAfterReadingHistory()
    {
        var store = new FileWorkshopDownloadStore(_root);
        var previous = new WorkshopDownloadSnapshot(Guid.NewGuid(), Ck3.AppId, 101, "旧任务",
            WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow.AddMinutes(-1))
            { Owner = WorkshopDownloadOwner.SteamClient };
        await store.SaveTaskAsync(previous, Token);
        var latest = previous with { UpdatedAt = previous.UpdatedAt.AddSeconds(10), Title = "重新检查" };
        await store.SaveTaskAsync(latest, Token);
        await store.DeleteSteamItemTaskIfUnchangedAsync(previous, Token);
        Assert.Equal(latest, Assert.Single(await store.ReadTasksAsync(Token)));
        await store.DeleteSteamItemTaskIfUnchangedAsync(latest, Token);
        Assert.Empty(await store.ReadTasksAsync(Token));
    }

    [Fact]
    public async Task SteamItemHistoryRemovalCanRunForDifferentItemsConcurrently()
    {
        var store = new FileWorkshopDownloadStore(_root);
        for (var round = 0; round < 20; round++)
        {
            for (var index = 0; index < 12; index++)
                foreach (var itemId in new ulong[] { 101, 202 })
                    await store.SaveTaskAsync(new(Guid.NewGuid(), Ck3.AppId, itemId, "native",
                        WorkshopDownloadState.Completed, 0, null, DateTimeOffset.UtcNow)
                        { Owner = WorkshopDownloadOwner.SteamClient }, Token);
            await Task.WhenAll(store.DeleteSteamItemTasksAsync(Ck3.AppId, 101, Token),
                store.DeleteSteamItemTasksAsync(Ck3.AppId, 202, Token));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "downloads", Ck3.AppId.ToString()), "*.json"));
        }
    }

    [Theory]
    [InlineData(16, 4, 12)]
    [InlineData(24, 8, 16)]
    public void NativeDownloadResultSupportsBothPlatformLayouts(int length, int itemOffset, int resultOffset)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Ck3.AppId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(itemOffset), 3830179207);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(resultOffset), 1);
        var result = NativeSteamClientApi.DecodeDownload(bytes);
        Assert.Equal(new SteamClientResult("download", Ck3.AppId, 3830179207, 1), result);
        Assert.Throws<InvalidDataException>(() => NativeSteamClientApi.DecodeDownload(new byte[17]));
    }

    [Fact]
    public void ProtocolIgnoresSdkDiagnosticsAndRejectsMalformedOrOversizedFrames()
    {
        Assert.Null(SteamClientFrame.Decode("[Steam SDK] unrelated diagnostic"));
        var frame = new SteamClientFrame("progress", Ck3.AppId, 101, 16, 5, 12);
        Assert.Equal(frame, SteamClientFrame.Decode(frame.Encode()));
        Assert.Throws<InvalidDataException>(() => SteamClientFrame.Decode(SteamClientFrame.Prefix + "{"));
        Assert.Throws<InvalidDataException>(() => SteamClientFrame.Decode(SteamClientFrame.Prefix + new string('x', 65536)));
    }

    [Fact]
    public void CompletionValidationRejectsEmptyOrTruncatedDescriptionsAndSupportsMetadata()
    {
        var descriptor = Path.Combine(_root, "descriptor.mod");
        File.WriteAllText(descriptor, ""); Assert.False(Ck3ModScanner.IsRecognizableWorkshopContent(_root, 101));
        File.WriteAllText(descriptor, "name=\"test\""); Assert.True(Ck3ModScanner.IsRecognizableWorkshopContent(_root, 101));
        File.Delete(descriptor); Directory.CreateDirectory(Path.Combine(_root, ".metadata"));
        var metadata = Path.Combine(_root, ".metadata", "metadata.json");
        File.WriteAllText(metadata, "{}"); Assert.False(Ck3ModScanner.IsRecognizableWorkshopContent(_root, 101));
        File.WriteAllText(metadata, "{\"name\":\"test\"}"); Assert.True(Ck3ModScanner.IsRecognizableWorkshopContent(_root, 101));
    }

    [Fact]
    public void MachOArchitectureProbeAcceptsCurrentSliceOnly()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var current = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x0100000Cu : 0x01000007u;
        var other = current == 0x0100000Cu ? 0x01000007u : 0x0100000Cu;
        Assert.True(Probe(current));
        Assert.False(Probe(other));

        bool Probe(uint cpu)
        {
            var path = Path.Combine(_root, $"probe-{cpu}.dylib");
            using (var stream = File.Create(path))
            {
                Span<byte> header = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(header, 0xCAFEBABE);
                BinaryPrimitives.WriteUInt32BigEndian(header[4..], 1);
                stream.Write(header);
                Span<byte> architecture = stackalloc byte[20];
                BinaryPrimitives.WriteUInt32BigEndian(architecture, cpu);
                stream.Write(architecture);
            }
            return SteamClientLibraryLocator.SupportsProcessArchitecture(path);
        }
    }

    [Fact]
    public async Task PrisonArchitectDownloadAcceptsUgcContainerAndRejectsEmptyContent()
    {
        var container = Path.Combine(_root, "legacy.bin");
        File.WriteAllBytes(container, [1, 2, 3]);
        var gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        var library = OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : OperatingSystem.IsWindows() ? "steam_api64.dll" : "libsteam_api.so";
        File.WriteAllText(Path.Combine(gameRoot, library), "test-library");
        var fixture = new EnvironmentStub(gameRoot);
        var item = new WorkshopItem(123, GameCatalog.PrisonArchitect.AppId, "test", "", null,
            new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=123"), DateTimeOffset.UtcNow, 4, [], null);
        async IAsyncEnumerable<SteamClientFrame> Frames(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new("connected", request.AppId, request.ItemId);
            yield return new("complete", request.AppId, request.ItemId, 5, 0, 0, container, 3);
        }
        var downloader = new SteamClientWorkshopDownloader(GameCatalog.PrisonArchitect, fixture, fixture, _root, Frames);

        var result = await downloader.DownloadAsync(item, new Recorder(new List<WorkshopTransferProgress>()), Token);
        Assert.Equal(container, result.ContentPath);
        Assert.Equal(3, result.SizeOnDisk);

        File.WriteAllBytes(container, []);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(item, new Recorder(new List<WorkshopTransferProgress>()), Token));
    }

    [Fact]
    public void NewSubscriptionTracksSteamEvenBeforeTheQueueStateAppears()
    {
        var api = new FakeApi(_root) { State = 0 };
        api.Frame = frame => frame switch
        {
            1 => ConfirmSubscription(api),
            2 => [new("download", 99, 101, 9), new("download", Ck3.AppId, 202, 9)],
            3 => FinishDownload(api),
            _ => []
        };
        var frames = new List<SteamClientFrame>();
        SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, frames.Add, Token, TimeSpan.Zero);
        Assert.Equal(1, api.Subscriptions);
        Assert.Equal(0, api.Downloads);
        Assert.Equal(3, api.FrameNumber);
        Assert.Contains(frames, f => f.Type == "progress" && f.Bytes == 40 && f.TotalBytes == 100);
        var complete = Assert.Single(frames, f => f.Type == "complete");
        Assert.Equal(40, complete.Bytes); Assert.Equal(100, complete.TotalBytes);
        Assert.Equal(_root, complete.ContentPath); Assert.Equal(500, complete.SizeOnDisk);
        Assert.False(complete.Reused);
    }

    private static SteamClientResult[] ConfirmSubscription(FakeApi api)
    {
        api.State = SteamClientDownloadLoop.Subscribed;
        return [new("subscription", Ck3.AppId, 101, 1)];
    }

    private static SteamClientResult[] FinishDownload(FakeApi api)
    {
        api.State = SteamClientDownloadLoop.Subscribed | SteamClientDownloadLoop.Installed;
        return [new("download", Ck3.AppId, 101, 1)];
    }

    [Fact]
    public void StalledSteamDownloadStopsTrackingWithoutResubmitting()
    {
        var api = new FakeApi(_root) { State = SteamClientDownloadLoop.Subscribed | SteamClientDownloadLoop.Pending, Progress = null };
        var error = Assert.Throws<TimeoutException>(() =>
            SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, _ => { }, Token, TimeSpan.Zero, idleTimeout: TimeSpan.Zero));
        Assert.Contains("没有进展", error.Message);
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public void AlreadyInstalledContentIsReusedWithoutDownloadOrSubscription()
    {
        var api = new FakeApi(_root) { State = 5 };
        var frames = new List<SteamClientFrame>();
        SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, frames.Add, Token, TimeSpan.Zero);
        Assert.Equal(0, api.Downloads); Assert.Equal(0, api.Subscriptions);
        var completed = Assert.Single(frames);
        Assert.True(completed.Reused); Assert.Null(completed.TotalBytes); Assert.Equal(0, completed.Bytes);
    }

    [Fact]
    public void ExistingSteamDownloadIsOnlyTrackedWithoutResubmittingDownload()
    {
        var api = new FakeApi(_root) { State = 49 };
        api.Frame = n => { if (n == 3) api.State = 5; return n == 2 ? [new("download", Ck3.AppId, 101, 1)] : []; };
        var frames = new List<SteamClientFrame>();
        SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, frames.Add, Token, TimeSpan.Zero);
        Assert.Equal(0, api.Downloads); Assert.Equal(0, api.Subscriptions);
        Assert.Equal(3, api.FrameNumber);
        Assert.Single(frames, f => f.Type == "complete");
    }

    [Theory]
    [InlineData("subscription", 0)]
    [InlineData("download", 1)]
    public void SteamFailureNeverBecomesInstalled(string kind, uint state)
    {
        var api = new FakeApi(_root) { State = state };
        api.Frame = n => n == 2 ? [new(kind, Ck3.AppId, 101, 9)] : [];
        Assert.Throws<IOException>(() => SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, _ => { }, Token, TimeSpan.Zero));
    }

    [Fact]
    public void AcceptedRequestAloneNeverCompletesAndCancellationOnlyStopsTracking()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var api = new FakeApi(_root);
        api.Frame = n => { if (n == 3) cancellation.Cancel(); return []; };
        var frames = new List<SteamClientFrame>();
        Assert.Throws<OperationCanceledException>(() => SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, frames.Add, cancellation.Token, TimeSpan.Zero));
        Assert.Equal(1, api.Downloads); Assert.DoesNotContain(frames, f => f.Type == "complete");
    }

    [Fact]
    public void MissingFilesAndUnconfirmedSubscriptionCannotPass()
    {
        var api = new FakeApi(Path.Combine(_root, "missing")) { State = 5 };
        Assert.Throws<InvalidDataException>(() => SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, _ => { }, Token, TimeSpan.Zero));
        api = new FakeApi(_root) { State = 0 };
        Assert.Throws<TimeoutException>(() => SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, _ => { }, Token, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public void SteamExitEndsTrackingInsteadOfHoldingQueueForever()
    {
        var api = new FakeApi(_root);
        api.Frame = n => { if (n == 2) api.IsRunning = false; return []; };
        var error = Assert.Throws<IOException>(() => SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, _ => { }, Token, TimeSpan.Zero));
        Assert.Contains("Steam 客户端已退出", error.Message); Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public void UnknownDownloadSizeIsNotReplacedByInstalledSize()
    {
        var api = new FakeApi(_root) { Progress = new(0, null) };
        api.Frame = n => n == 2 ? [new("download", Ck3.AppId, 101, 1)] : [];
        var frames = new List<SteamClientFrame>();
        SteamClientDownloadLoop.Run(api, Ck3.AppId, 101, frames.Add, Token, TimeSpan.Zero);
        var complete = Assert.Single(frames, f => f.Type == "complete");
        Assert.Null(complete.TotalBytes); Assert.Equal(0, complete.Bytes); Assert.Equal(500, complete.SizeOnDisk);
    }

    [Fact]
    public void LocatorPrefersActualGameFrameworkOverOldGameDirectoryLibrary()
    {
        var name = OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : OperatingSystem.IsWindows() ? "steam_api64.dll" : "libsteam_api.so";
        var preferred = OperatingSystem.IsMacOS() ? "binaries/ck3.app/Contents/Frameworks" : "binaries";
        Directory.CreateDirectory(Path.Combine(_root, preferred)); Directory.CreateDirectory(Path.Combine(_root, "game"));
        File.WriteAllText(Path.Combine(_root, preferred, name), "current");
        File.WriteAllText(Path.Combine(_root, "game", name), "old");
        Assert.Equal(Path.Combine(_root, preferred, name), SteamClientLibraryLocator.Find(GameCatalog.Ck3, _root));
    }

    [Fact]
    public async Task ParentRequiresIdentityAndCleanTransportCompletionAndUsesNoAuthentication()
    {
        File.WriteAllText(Path.Combine(_root, "descriptor.mod"), "name=\"test\"");
        var gameRoot = Path.Combine(_root, "game"); Directory.CreateDirectory(gameRoot);
        var name = OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : OperatingSystem.IsWindows() ? "steam_api64.dll" : "libsteam_api.so";
        File.WriteAllText(Path.Combine(gameRoot, name), "test-library");
        var fixture = new EnvironmentStub(gameRoot);
        var item = new WorkshopItem(101, Ck3.AppId, "test", "", null, new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=101"), DateTimeOffset.UtcNow, 4, [], null);
        async IAsyncEnumerable<SteamClientFrame> Frames(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); await Task.Yield();
            yield return new("connected", request.AppId, request.ItemId);
            yield return new("progress", request.AppId, request.ItemId, 16, 40, 100);
            yield return new("complete", request.AppId, request.ItemId, 5, 40, 100, _root, 500);
        }
        var progress = new List<WorkshopTransferProgress>();
        var downloader = new SteamClientWorkshopDownloader(GameCatalog.Ck3, fixture, fixture, _root, Frames);
        var result = await downloader.DownloadAsync(item, new Recorder(progress), Token);
        Assert.Equal(_root, result.ContentPath); Assert.Equal(500, result.SizeOnDisk);
        Assert.Equal(40, result.DownloadedBytes); Assert.Equal(100, result.DownloadTotalBytes);
        Assert.All(progress, p => { Assert.Null(p.AuthenticationUrl); Assert.NotEqual(WorkshopDownloadState.AwaitingAuthentication, p.State); });
        async IAsyncEnumerable<SteamClientFrame> Wrong(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        { await Task.Yield(); yield return new("complete", 99, request.ItemId, ContentPath: _root, SizeOnDisk: 5); }
        downloader = new(GameCatalog.Ck3, fixture, fixture, _root, Wrong);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(item, new Recorder(progress), Token));
        async IAsyncEnumerable<SteamClientFrame> Crashed(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        { await Task.Yield(); yield return new("complete", request.AppId, request.ItemId, ContentPath: _root, SizeOnDisk: 5); throw new IOException("child failed"); }
        downloader = new(GameCatalog.Ck3, fixture, fixture, _root, Crashed);
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(item, new Recorder(progress), Token));
        File.Delete(Path.Combine(_root, "descriptor.mod"));
        downloader = new(GameCatalog.Ck3, fixture, fixture, _root, Frames);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(item, new Recorder(progress), Token));
    }

    [Fact]
    public async Task SilentConnectedBridgeTimesOutAndReceivesStop()
    {
        if (OperatingSystem.IsWindows()) return;
        var stopped = Path.Combine(_root, "bridge-stop.txt");
        var process = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add("""printf '%s\n' "$1"; read -r command; printf '%s' "$command" > "$2" """);
        process.ArgumentList.Add("test-bridge");
        process.ArgumentList.Add(new SteamClientFrame("connected", Ck3.AppId, 101).Encode());
        process.ArgumentList.Add(stopped);
        var frames = new List<SteamClientFrame>();
        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var frame in SteamClientWorkshopDownloader.ReadProcessFramesAsync(process, TimeSpan.FromMilliseconds(150), Token))
                frames.Add(frame);
        });
        Assert.Contains("长时间无响应", error.Message);
        Assert.Equal("connected", Assert.Single(frames).Type);
        Assert.Equal("stop", await File.ReadAllTextAsync(stopped, Token));
    }

    [Fact]
    public void UnsubscribeRequiresMatchingSuccessAndWaitsUntilSteamStopsTrackingDownload()
    {
        var api = new FakeApi(_root) { State = SteamClientDownloadLoop.Subscribed | SteamClientDownloadLoop.Downloading };
        api.Frame = frame =>
        {
            if (frame == 1) return [new("unsubscription", Ck3.AppId, 202, 1)];
            if (frame == 2)
            {
                api.State = SteamClientDownloadLoop.Downloading;
                return [new("unsubscription", Ck3.AppId, 101, 1)];
            }
            api.State = SteamClientDownloadLoop.Installed;
            return [];
        };
        SteamClientWorker.UnsubscribeItem(api, Ck3.AppId, 101, Token);
        Assert.Equal(1, api.Unsubscriptions);
        Assert.Equal(3, api.FrameNumber);
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public void UnsubscribeFailureDoesNotCountAsConfirmation()
    {
        var api = new FakeApi(_root);
        api.Frame = _ => [new("unsubscription", Ck3.AppId, 101, 15)];
        Assert.Throws<IOException>(() => SteamClientWorker.UnsubscribeItem(api, Ck3.AppId, 101, Token));
    }

    [Fact]
    public void StatusWorkerReadsEachRequestedItemWithoutSubscriptionOrDownload()
    {
        var api = new FakeApi(_root) { StateFor = item => item == 101 ? 13u : 49u };
        var frames = new List<SteamClientFrame>();
        SteamClientWorker.ReadItemStates(api, Ck3.AppId, [101, 202], frames.Add, Token);
        Assert.Equal(new[] { (101UL, 13U), (202UL, 49U) }, frames.Select(frame => (frame.ItemId, frame.State)));
        Assert.All(frames, frame => Assert.Equal("state", frame.Type));
        Assert.Equal(0, api.Subscriptions);
        Assert.Equal(0, api.Downloads);
        Assert.Equal(0, api.FrameNumber);
        api.IsRunning = false;
        Assert.Throws<IOException>(() => SteamClientWorker.ReadItemStates(api, Ck3.AppId, [101], frames.Add, Token));
        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public async Task StatusBridgeUsesOneRequestAndClassifiesOnlySteamClientStates()
    {
        var name = OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : OperatingSystem.IsWindows() ? "steam_api64.dll" : "libsteam_api.so";
        File.WriteAllText(Path.Combine(_root, name), "test-library");
        var fixture = new EnvironmentStub(_root);
        var environment = await fixture.DiscoverAsync(new(), Token);
        var calls = 0;
        async IAsyncEnumerable<SteamClientFrame> Frames(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            calls++;
            Assert.Equal(new ulong[] { 101, 202, 303, 404 }, request.StatusItems!.Order());
            Assert.Equal(SteamClientWorker.StatusCommand,
                SteamClientWorkshopDownloader.CreateStartInfo(request).ArgumentList[^4]);
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new("connected", Ck3.AppId, 0);
            yield return new("state", Ck3.AppId, 101, State: 13);
            yield return new("state", Ck3.AppId, 202, State: 45);
            yield return new("state", Ck3.AppId, 303, State: 5);
            yield return new("state", Ck3.AppId, 404, State: 8);
        }
        var steam = new SteamClientWorkshopDownloader(GameCatalog.Ck3, fixture, fixture, _root, Frames);
        var result = await steam.CheckAsync(environment, [101, 202, 101, 303, 404], Token);
        Assert.Equal(1, calls);
        Assert.Null(result.Note);
        Assert.Equal(WorkshopUpdateState.NeedsUpdate, result.States[101]);
        Assert.Equal(WorkshopUpdateState.Processing, result.States[202]);
        Assert.Equal(WorkshopUpdateState.NoPendingUpdate, result.States[303]);
        Assert.Equal(WorkshopUpdateState.Unknown, result.States[404]);

        async IAsyncEnumerable<SteamClientFrame> Offline(SteamClientRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new("error", request.AppId, 0, Error: "无法连接 Steam 客户端");
        }
        steam = new(GameCatalog.Ck3, fixture, fixture, _root, Offline);
        var unavailable = await steam.CheckAsync(environment, [101, 202], Token);
        Assert.Empty(unavailable.States);
        Assert.Contains("无法连接 Steam", unavailable.Note);
    }

    private sealed class FakeApi(string path) : ISteamClientApi
    {
        public uint AppId => Ck3.AppId;
        public bool IsRunning { get; set; } = true;
        public uint State = 1;
        public Func<ulong, uint>? StateFor { get; init; }
        public int Subscriptions, Unsubscriptions, Downloads, FrameNumber;
        public SteamClientProgress? Progress = new(40, 100);
        public Func<int, IReadOnlyList<SteamClientResult>> Frame = _ => [];
        public uint GetItemState(ulong item) => StateFor?.Invoke(item) ?? State;
        public bool SubscribeItem(ulong item) { Subscriptions++; return true; }
        public bool DownloadItem(ulong item) { Downloads++; return true; }
        public bool UnsubscribeItem(ulong item) { Unsubscriptions++; return true; }
        public IReadOnlyList<SteamClientResult> RunFrame()
        { if (++FrameNumber > 100) throw new InvalidOperationException("test loop exceeded"); return Frame(FrameNumber); }
        public SteamClientProgress? GetProgress(ulong item) => Progress;
        public SteamClientInstalled? GetInstalled(ulong item) => new(path, 500);
        public void Dispose() { }
    }
    private sealed class EnvironmentStub(string gameRoot) : IGameLocator, ISettingsStore
    {
        public Task<GameEnvironment> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new GameEnvironment(gameRoot, gameRoot, "1.20", [], []));
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Recorder(List<WorkshopTransferProgress> progress) : IProgress<WorkshopTransferProgress>
    { public void Report(WorkshopTransferProgress value) => progress.Add(value); }
    public void Dispose() => Directory.Delete(_root, true);
}
