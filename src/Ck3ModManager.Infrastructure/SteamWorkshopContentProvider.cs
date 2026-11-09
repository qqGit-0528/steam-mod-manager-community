using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Ck3ModManager.Core;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamKit2.CDN;

namespace Ck3ModManager.Infrastructure;

/// <summary>独立的 Steam 网络会话；在后台获取工坊 manifest 与 CDN 文件，不打开外部页面。</summary>
public sealed class SteamWorkshopContentProvider : IWorkshopContentProvider, IDisposable
{
    private readonly bool allowAuthentication;
    private readonly SteamDownloadAuthentication _authentication;
    private readonly bool _ownsAuthentication;
    private bool _disposed;

    public SteamWorkshopContentProvider(bool allowAuthentication = true, SteamDownloadAuthentication? authentication = null)
    {
        this.allowAuthentication = allowAuthentication;
        _authentication = authentication ?? new SteamDownloadAuthentication();
        _ownsAuthentication = authentication is null;
    }
    private sealed class AuthenticationNeededException : Exception;
    private sealed record Transfer(Session Session, uint DepotId, byte[] Key, DepotManifest Manifest, IReadOnlyList<Server> Servers);
    private Session? _session;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    public async Task<WorkshopContentPlan> ResolveAsync(WorkshopItem item, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session ??= await OpenSessionAsync(false, progress, cancellationToken).ConfigureAwait(false);
            try { return await ResolveCoreAsync(item, _session, cancellationToken).ConfigureAwait(false); }
            catch (AuthenticationNeededException) when (!_session.Authenticated)
            {
                if (!allowAuthentication) throw new UnauthorizedAccessException("此工坊内容需要 Steam 账户认证，请在应用内扫码后下载。");
                _session.Dispose();
                _session = null;
                _session = await OpenSessionAsync(true, progress, cancellationToken).ConfigureAwait(false);
                progress.Report(new(WorkshopDownloadState.Resolving, Message: "认证完成，正在获取内容清单"));
                return await ResolveCoreAsync(item, _session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AuthenticationNeededException) { Reset(); throw new UnauthorizedAccessException("Steam 拒绝读取此内容，请确认扫码账户拥有游戏且可以访问该 Mod。"); }
        catch (Exception error) when (error is not OutOfMemoryException) { Reset(); throw; }
    }

    private static async Task<WorkshopContentPlan> ResolveCoreAsync(WorkshopItem item, Session session, CancellationToken token)
    {
        var request = new CPublishedFile_GetDetails_Request { appid = item.ConsumerAppId, includetags = true };
        request.publishedfileids.Add(item.PublishedFileId);
        var response = await session.PublishedFiles.GetDetails(request).ToTask().WaitAsync(RequestTimeout, token).ConfigureAwait(false);
        var details = response.Body.publishedfiledetails.SingleOrDefault();
        if (response.Result != EResult.OK || details is null || details.result != 1)
        {
            if (!session.Authenticated) throw new AuthenticationNeededException();
            throw new InvalidDataException("Steam 未提供此 Mod 的内容，物品可能已删除、设为私有或当前账户无权限。");
        }
        if (details.consumer_appid != item.ConsumerAppId || details.publishedfileid != item.PublishedFileId)
            throw new InvalidDataException("Steam 内容的游戏或工坊身份与请求不一致。");
        if (details.file_type == (uint)EWorkshopFileType.Collection) throw new InvalidDataException("工坊合集不是单个 Mod，请分别选择合集中的发布项。");
        if (details.hcontent_file == 0) throw new NotSupportedException("此工坊发布未提供可验证的内容清单，暂不支持旧式直链发布。");

        var apps = session.Apps;
        var access = await apps.PICSGetAccessTokens([item.ConsumerAppId], []).ToTask().WaitAsync(RequestTimeout, token).ConfigureAwait(false);
        var appRequest = new SteamApps.PICSRequest(item.ConsumerAppId);
        if (access.AppTokens.TryGetValue(item.ConsumerAppId, out var accessToken)) appRequest.AccessToken = accessToken;
        var info = await apps.PICSGetProductInfo([appRequest], []).ToTask().WaitAsync(RequestTimeout, token).ConfigureAwait(false);
        var app = info.Results?.SelectMany(result => result.Apps).FirstOrDefault(pair => pair.Key == item.ConsumerAppId).Value;
        if (app is null) throw new AuthenticationNeededException();
        var depotId = app.KeyValues["depots"]["workshopdepot"].AsUnsignedInteger();
        if (depotId == 0) depotId = item.ConsumerAppId;
        var key = await apps.GetDepotDecryptionKey(depotId, item.ConsumerAppId).ToTask().WaitAsync(RequestTimeout, token).ConfigureAwait(false);
        if (key.Result != EResult.OK) throw new AuthenticationNeededException();
        var code = await session.Content.GetManifestRequestCode(depotId, item.ConsumerAppId, details.hcontent_file).WaitAsync(RequestTimeout, token).ConfigureAwait(false);
        if (code == 0) throw new AuthenticationNeededException();
        var servers = (await session.Content.GetServersForSteamPipe().WaitAsync(RequestTimeout, token).ConfigureAwait(false))
            .Where(server => server.Type is "CDN" or "SteamCache" && (server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(item.ConsumerAppId)))
            .OrderBy(server => server.WeightedLoad).Take(8)
            // SDK 会把支持可选 HTTPS 的节点默认标成 HTTP；始终在 443 上通过证书校验连接，不降级。
            .Where(server => !string.IsNullOrWhiteSpace(server.VHost ?? server.Host))
            .Select(server => (Server)new DnsEndPoint(server.VHost ?? server.Host!, 443)).ToArray();
        if (servers.Length == 0) throw new IOException("Steam 暂未返回可用的内容服务器，请稍后重试。");
        var manifest = await FromCdnAsync(session, item.ConsumerAppId, depotId, servers,
            (server, auth) => session.Cdn.DownloadManifestAsync(depotId, details.hcontent_file, code, server, key.DepotKey, cdnAuthToken: auth), token).ConfigureAwait(false);
        if (manifest.ManifestGID != details.hcontent_file || manifest.DepotID != depotId || manifest.FilenamesEncrypted || manifest.Files is null)
            throw new InvalidDataException("Steam 内容清单身份不一致或无法解密文件名。");
        if (manifest.Files.Any(file => file.LinkTarget is { Length: > 0 } || (file.Flags & EDepotFileFlag.Symlink) != 0))
            throw new InvalidDataException("Mod 清单包含符号链接，暂不支持安全安装。");
        var files = manifest.Files.Where(file => (file.Flags & EDepotFileFlag.Directory) == 0)
            .Select(file => new WorkshopContentFile(file.FileName.Replace('\\', '/'), checked((long)file.TotalSize), Convert.ToHexString(file.FileHash))).ToArray();
        if (files.Length is 0 or > 200000 || files.Any(file => file.Sha1.Length != 40)) throw new InvalidDataException("Steam 返回的 Mod 文件清单无效。");
        // 下载前检查所有相对路径和大小，不能等网络传输完才发现路径越界。
        // 预检只检查清单路径语法；真实目录和符号链接在 staging/安装时检查，与 App 启动目录无关。
        foreach (var file in files) FileWorkshopDownloadStore.NormalizeRelativePath(file.RelativePath);
        var paths = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count != files.Length || !paths.Contains("descriptor.mod")) throw new InvalidDataException("文件路径冲突或缺少 descriptor.mod，无法安装此 Mod。");
        return new(item.ConsumerAppId, item.PublishedFileId, $"{depotId}:{details.hcontent_file}", files,
            new Transfer(session, depotId, key.DepotKey, manifest, servers));
    }

    public async Task DownloadAsync(WorkshopContentPlan plan, string stagingDirectory, IProgress<WorkshopTransferProgress> progress, CancellationToken cancellationToken)
    {
        if (plan.Transport is not Transfer transfer) throw new InvalidOperationException("下载计划不属于此 Steam 下载器。");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var total = plan.TotalBytes;
            var completed = 0L;
            var transferred = 0L;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.MinValue;
            void Report(bool force = false)
            {
                if (force || clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                { lastReport = clock.Elapsed; progress.Report(new(WorkshopDownloadState.Downloading, completed, total, "正在从 Steam 下载内容", TransferredBytes: transferred)); }
            }
            Report(true);
            foreach (var file in transfer.Manifest.Files!.Where(file => (file.Flags & EDepotFileFlag.Directory) == 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = FileWorkshopDownloadStore.SafeFilePath(stagingDirectory, file.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // 已完成文件按 manifest 的 SHA-1 复核后复用；半文件按本次清单重新下载。
                if (File.Exists(path) && new FileInfo(path).Length == checked((long)file.TotalSize))
                {
                    await using var existing = File.OpenRead(path);
                    if ((await SHA1.HashDataAsync(existing, cancellationToken).ConfigureAwait(false)).SequenceEqual(file.FileHash))
                    { completed += checked((long)file.TotalSize); Report(); continue; }
                }
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                var offset = 0UL;
                foreach (var chunk in file.Chunks.OrderBy(chunk => chunk.Offset))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (chunk.Offset != offset || chunk.UncompressedLength is 0 or > 16 * 1024 * 1024 || chunk.Offset + chunk.UncompressedLength > file.TotalSize)
                        throw new InvalidDataException("Steam 文件分块范围无效。");
                    var buffer = new byte[chunk.UncompressedLength];
                    var written = await FromCdnAsync(transfer.Session, plan.AppId, transfer.DepotId, transfer.Servers,
                        (server, auth) => transfer.Session.Cdn.DownloadDepotChunkAsync(transfer.DepotId, chunk, server, buffer, transfer.Key, cdnAuthToken: auth), cancellationToken).ConfigureAwait(false);
                    if (written != chunk.UncompressedLength) throw new InvalidDataException("Steam 返回的文件分块长度不符。");
                    await output.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                    offset += (uint)written;
                    completed += written;
                    transferred += chunk.CompressedLength;
                    Report();
                }
                if (offset != file.TotalSize) throw new InvalidDataException("Steam 文件分块未覆盖完整内容。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            Report(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Reset(); throw; }
    }

    private static async Task<T> FromCdnAsync<T>(Session session, uint appId, uint depotId, IReadOnlyList<Server> servers,
        Func<Server, string?, Task<T>> action, CancellationToken token)
    {
        Exception? last = null;
        foreach (var server in servers.Take(4))
        {
            token.ThrowIfCancellationRequested();
            try { return await action(server, null).WaitAsync(RequestTimeout, token).ConfigureAwait(false); }
            catch (SteamKitWebRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden)
            {
                var auth = await session.Content.GetCDNAuthToken(appId, depotId, server.Host!).WaitAsync(RequestTimeout, token).ConfigureAwait(false);
                if (auth.Result != EResult.OK) throw new UnauthorizedAccessException("Steam 内容服务器拒绝访问，请确认账户权限。");
                try { return await action(server, auth.Token).WaitAsync(RequestTimeout, token).ConfigureAwait(false); }
                catch (TimeoutException) { session.Cdn.Dispose(); throw; }
                catch (OperationCanceledException timeout) when (!token.IsCancellationRequested) { last = timeout; }
                catch (Exception retryError) when (retryError is HttpRequestException) { last = retryError; }
            }
            catch (TimeoutException) { session.Cdn.Dispose(); throw; }
            catch (OperationCanceledException timeout) when (!token.IsCancellationRequested) { last = timeout; }
            catch (Exception error) when (error is HttpRequestException) { last = error; }
        }
        throw new IOException("Steam 内容服务器连接失败，请检查网络后重试。", last);
    }

    private async Task<Session> OpenSessionAsync(bool authenticate, IProgress<WorkshopTransferProgress> progress, CancellationToken token)
    {
        var session = await _authentication.ConnectAsync(async (credentials, ct) =>
        {
            if (credentials is not null)
                progress.Report(new(WorkshopDownloadState.Resolving, Message: "正在复用下载授权连接 Steam"));
            var opened = await Session.OpenAsync(authenticate, credentials, progress, ct).ConfigureAwait(false);
            return (opened, opened.Credentials);
        }, token).ConfigureAwait(false);
        if (_disposed) { session.Dispose(); throw new ObjectDisposedException(nameof(SteamWorkshopContentProvider)); }
        return session;
    }

    private void Reset() { _session?.Dispose(); _session = null; }
    public void Dispose()
    {
        _disposed = true;
        Reset();
        if (_ownsAuthentication) _authentication.Clear();
    }

    private sealed class Session : IDisposable
    {
        private readonly SteamClient _client = new(SteamConfiguration.Create(builder => builder
            .WithProtocolTypes(ProtocolTypes.WebSocket)
            .WithHttpClientFactory(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }))));
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<IDisposable> _subscriptions = [];
        public SteamApps Apps { get; }
        public SteamContent Content { get; }
        public PublishedFile PublishedFiles { get; }
        public Client Cdn { get; }
        public bool Authenticated { get; private set; }
        public SteamSessionCredentials? Credentials { get; private set; }

        private Session()
        {
            Apps = _client.GetHandler<SteamApps>()!;
            Content = _client.GetHandler<SteamContent>()!;
            PublishedFiles = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
            Cdn = new Client(_client);
        }

        public static async Task<Session> OpenAsync(bool authenticate, SteamSessionCredentials? credentials,
            IProgress<WorkshopTransferProgress> progress, CancellationToken token)
        {
            var session = new Session();
            try
            {
                var manager = new CallbackManager(session._client);
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                session._subscriptions.Add(manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult()));
                session._subscriptions.Add(manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
                { connected.TrySetException(new IOException("Steam 网络连接已断开。")); loggedOn.TrySetException(new IOException("Steam 登录连接已断开。")); }));
                session._subscriptions.Add(manager.Subscribe<SteamUser.LoggedOnCallback>(result => loggedOn.TrySetResult(result.Result)));
                _ = Task.Run(async () =>
                {
                    try { while (!session._lifetime.IsCancellationRequested) await manager.RunWaitCallbackAsync(session._lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception error) when (error is not OutOfMemoryException) { connected.TrySetException(error); loggedOn.TrySetException(error); }
                });
                session._client.Connect();
                await connected.Task.WaitAsync(RequestTimeout, token).ConfigureAwait(false);
                var user = session._client.GetHandler<SteamUser>()!;
                if (authenticate || credentials is not null)
                {
                    if (credentials is null)
                    {
                        var auth = await session._client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                        { DeviceFriendlyName = "Paradox Mod Manager", IsPersistentSession = false }).WaitAsync(RequestTimeout, token).ConfigureAwait(false);
                        void ShowChallenge() => progress.Report(new(WorkshopDownloadState.AwaitingAuthentication,
                            Message: "请使用 Steam 手机应用扫码，授权本工具下载。", AuthenticationUrl: auth.ChallengeURL));
                        auth.ChallengeURLChanged = ShowChallenge;
                        ShowChallenge();
                        try
                        {
                            var login = await auth.PollingWaitForResultAsync(token).WaitAsync(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
                            credentials = new SteamSessionCredentials(login.AccountName, login.RefreshToken);
                        }
                        catch (AuthenticationException error) when (error.Result == EResult.FileNotFound || error.Result == EResult.Expired)
                        { throw new IOException("下载授权二维码已失效，请重试后扫描新二维码。", error); }
                        finally { auth.ChallengeURLChanged = static () => { }; }
                    }
                    user.LogOn(new SteamUser.LogOnDetails { Username = credentials.AccountName, AccessToken = credentials.RefreshToken,
                        ShouldRememberPassword = false, LoginID = (uint)Random.Shared.Next(1, int.MaxValue) });
                }
                else user.LogOnAnonymous();
                var result = await loggedOn.Task.WaitAsync(RequestTimeout, token).ConfigureAwait(false);
                if (result is EResult.InvalidPassword or EResult.Expired or EResult.Revoked)
                    throw new SteamSessionRejectedException(result.ToString());
                if (result != EResult.OK) throw new IOException($"Steam 登录失败（{result}），请重试。");
                session.Authenticated = credentials is not null;
                session.Credentials = credentials;
                return session;
            }
            catch { session.Dispose(); throw; }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            Cdn.Dispose();
            _client.Disconnect();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
