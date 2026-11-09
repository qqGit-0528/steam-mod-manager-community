using static Ck3ModManager.Core.AppText;
namespace Ck3ModManager.Infrastructure;

internal static class SteamClientDownloadLoop
{
    internal const uint Subscribed = 1, Installed = 4, NeedsUpdate = 8, Downloading = 16, Pending = 32;

    internal static void Run(ISteamClientApi api, uint appId, ulong itemId, Action<SteamClientFrame> emit,
        CancellationToken token, TimeSpan? interval = null, TimeSpan? subscriptionTimeout = null, TimeSpan? idleTimeout = null)
    {
        if (api.AppId != appId) throw new InvalidDataException(T("TheSteamGameContextDoesNotMatch"));
        token.ThrowIfCancellationRequested();
        var state = api.GetItemState(itemId);
        var subscribed = (state & Subscribed) != 0;
        // 新订阅由 Steam 自行排队，订阅成功回调可能先于下载状态出现。
        var tracking = !subscribed || (state & (Downloading | Pending)) != 0;
        var downloadRequested = false;
        var downloadCompleted = false;
        var transferred = 0L;
        var lastObservedBytes = 0L;
        long? total = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var subscriptionLimit = subscriptionTimeout ?? TimeSpan.FromSeconds(45);
        var idleLimit = idleTimeout ?? TimeSpan.FromMinutes(10);
        var delay = interval ?? TimeSpan.FromMilliseconds(50);
        var lastReport = TimeSpan.MinValue;
        var lastActivity = TimeSpan.Zero;

        if (!subscribed)
        {
            emit(new("subscribing", appId, itemId, state));
            if (!api.SubscribeItem(itemId)) throw new IOException(T("SteamDidNotAcceptTheSubscriptionRequest"));
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!api.IsRunning) throw new IOException(T("SteamClosedAndDownloadTrackingEndedReopen"));
            foreach (var result in api.RunFrame())
            {
                if (result.AppId != appId || result.ItemId != itemId) continue;
                if (result.Kind == "subscription" && !subscribed)
                {
                    if (result.Result != 1) throw new IOException(Failure(T("Subscription"), result.Result));
                    subscribed = true;
                    lastActivity = started.Elapsed;
                }
                else if (result.Kind == "download" && tracking)
                {
                    if (result.Result != 1) throw new IOException(Failure(T("Download"), result.Result));
                    downloadCompleted = true;
                }
            }
            if (!subscribed && started.Elapsed >= subscriptionLimit)
                throw new TimeoutException(T("SteamDidNotConfirmTheSubscriptionIn"));

            var currentState = api.GetItemState(itemId);
            if (currentState != state) lastActivity = started.Elapsed;
            state = currentState;
            var cleanInstalled = (state & Installed) != 0 && (state & (NeedsUpdate | Downloading | Pending)) == 0;
            if (subscribed && (downloadRequested ? downloadCompleted : cleanInstalled))
            {
                var info = api.GetInstalled(itemId) ?? throw new IOException(T("SteamDidNotReturnInstallationDetailsFor"));
                if (!Path.IsPathFullyQualified(info.Path) || (!Directory.Exists(info.Path) && !File.Exists(info.Path)) || info.Size < 0)
                    throw new InvalidDataException(T("TheLocalContentReportedBySteamIs"));
                emit(new("complete", appId, itemId, state, transferred, total, info.Path, info.Size, !tracking));
                return;
            }
            if (subscribed && !tracking)
            {
                if ((state & (Downloading | Pending)) == 0)
                {
                    if (!api.DownloadItem(itemId)) throw new IOException(T("SteamDidNotAcceptTheDownloadRequest"));
                    downloadRequested = true;
                }
                tracking = true;
            }
            if (subscribed && (lastReport == TimeSpan.MinValue || started.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250)))
            {
                if (lastReport == TimeSpan.MinValue) emit(new("submitted", appId, itemId, state));
                var progress = api.GetProgress(itemId);
                if (progress is not null)
                {
                    if (progress.Bytes < 0 || progress.TotalBytes < 0) throw new InvalidDataException(T("SteamReturnedInvalidDownloadByteCounts"));
                    // Steam 在完成后清空计数；保留最后一次非零总量，不用安装大小冒充网络字节。
                    if (progress.Bytes > lastObservedBytes) lastActivity = started.Elapsed;
                    lastObservedBytes = progress.Bytes;
                    if (progress.TotalBytes is > 0) { transferred = progress.Bytes; total = progress.TotalBytes; }
                }
                emit(new("progress", appId, itemId, state, transferred, total));
                lastReport = started.Elapsed;
            }
            if (subscribed && started.Elapsed - lastActivity >= idleLimit)
                throw new TimeoutException(T("TheDownloadStoppedMakingProgressRetryTo"));
            if (delay > TimeSpan.Zero && token.WaitHandle.WaitOne(delay)) token.ThrowIfCancellationRequested();
        }
    }

    private static string Failure(string action, int result) => T("SteamFailedResult", action, result) + (result switch
    {
        3 => T("SteamIsOfflineCheckTheClientConnection"),
        9 => T("ThisContentIsMissingOrWasRemoved"),
        15 => T("ThisAccountDoesNotHaveAccessTo"),
        16 => T("TheSteamRequestTimedOutCheckThe2"),
        _ => T("CheckTheSteamClientSStatusAnd")
    });
}
