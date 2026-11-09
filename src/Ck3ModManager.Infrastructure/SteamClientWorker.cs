using static Ck3ModManager.Core.AppText;
using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public static class SteamClientWorker
{
    public const string Command = "--steam-client-worker";
    internal const string UnsubscribeCommand = "--steam-client-unsubscribe-worker";
    internal const string StatusCommand = "--steam-client-status-worker";
    internal const string LanguageVariable = "SMM_UI_LANGUAGE";
    internal const int MaxStatusItems = 1000;
    public static bool IsWorker(string[] args) => args.FirstOrDefault() is Command or StatusCommand or UnsubscribeCommand;

    public static int Run(string[] args)
    {
        if (args.Length != 4 || !IsWorker(args) || !uint.TryParse(args[1], out var appId) || GameCatalog.FindByAppId(appId) is null)
            return 2;
        var status = args[0] == StatusCommand;
        ulong itemId = 0;
        ulong[] items = [];
        if (status)
        {
            if (appId != GameCatalog.Ck3.AppId || ParseStatusItems(args[2]) is not { } parsed) return 2;
            items = parsed;
        }
        else if (!ulong.TryParse(args[2], out itemId) || itemId == 0) return 2;
        SetLanguage(Environment.GetEnvironmentVariable(LanguageVariable));
        using var cancellation = new CancellationTokenSource();
        if (Console.IsInputRedirected)
            _ = Task.Run(async () =>
            {
                try { await Console.In.ReadLineAsync(); }
                finally { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            });
        try
        {
            // 仅设置隔离桥接进程的 SDK 上下文；不读取或复制 Steam 账户凭据。
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var api = new NativeSteamClientApi(args[3], appId);
            Console.Out.WriteLine(new SteamClientFrame("connected", appId, itemId).Encode());
            void Emit(SteamClientFrame frame) => Console.Out.WriteLine(frame.Encode());
            if (status) ReadItemStates(api, appId, items, Emit, cancellation.Token);
            else if (args[0] == UnsubscribeCommand)
            {
                UnsubscribeItem(api, appId, itemId, cancellation.Token);
                Emit(new("unsubscribed", appId, itemId));
            }
            else SteamClientDownloadLoop.Run(api, appId, itemId, Emit, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                ? T("TheSteamLibraryShippedWithTheGame")
                : error.Message;
            Console.Out.WriteLine(new SteamClientFrame("error", appId, itemId, Error: message).Encode());
            return 1;
        }
    }

    internal static void UnsubscribeItem(ISteamClientApi api, uint appId, ulong itemId,
        CancellationToken token, TimeSpan? timeout = null)
    {
        if (api.AppId != appId) throw new InvalidDataException(T("TheSteamGameContextDoesNotMatch2"));
        token.ThrowIfCancellationRequested();
        if (!api.IsRunning) throw new IOException(T("OpenTheSteamClientFirst"));
        const uint subscribed = SteamClientDownloadLoop.Subscribed;
        const uint busy = SteamClientDownloadLoop.Downloading | SteamClientDownloadLoop.Pending;
        var confirmed = (api.GetItemState(itemId) & subscribed) == 0;
        if (!confirmed && !api.UnsubscribeItem(itemId))
            throw new IOException(T("SteamDidNotAcceptTheUnsubscribeRequest"));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!api.IsRunning) throw new IOException(T("SteamClosedBeforeConfirmingTheUnsubscribeRequest"));
            foreach (var result in api.RunFrame())
            {
                if (result.Kind != "unsubscription" || result.AppId != appId || result.ItemId != itemId) continue;
                if (result.Result != 1) throw new IOException(T("SteamUnsubscribeFailedResult", result.Result));
                confirmed = true;
            }
            if (confirmed && (api.GetItemState(itemId) & (subscribed | busy)) == 0) return;
            if (clock.Elapsed >= (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException(T("SteamHasNotConfirmedTheUnsubscribeOr"));
            if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50))) token.ThrowIfCancellationRequested();
        }
    }

    internal static void ReadItemStates(ISteamClientApi api, uint appId, IReadOnlyList<ulong> items,
        Action<SteamClientFrame> emit, CancellationToken token)
    {
        if (api.AppId != appId) throw new InvalidDataException(T("TheSteamGameContextDoesNotMatch3"));
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (!api.IsRunning) throw new IOException(T("SteamClosedAndSomeItemStatusesAre"));
            emit(new("state", appId, item, State: api.GetItemState(item)));
        }
    }

    private static ulong[]? ParseStatusItems(string value)
    {
        if (value.Length > MaxStatusItems * 21) return null;
        var fields = value.Split(',');
        if (fields.Length > MaxStatusItems) return null;
        var ids = new HashSet<ulong>();
        foreach (var field in fields)
            if (!ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 || !ids.Add(id))
                return null;
        return ids.ToArray();
    }
}
