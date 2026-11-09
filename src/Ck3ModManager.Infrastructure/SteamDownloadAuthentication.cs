namespace Ck3ModManager.Infrastructure;

/// <summary>本次 App 运行期间共享下载授权；不读写磁盘，不保留二维码。</summary>
public sealed class SteamDownloadAuthentication
{
    private readonly SemaphoreSlim _logon = new(1, 1);
    private readonly object _state = new();
    private SteamSessionCredentials? _credentials;
    private long _generation;

    internal async Task<T> ConnectAsync<T>(Func<SteamSessionCredentials?, CancellationToken,
        Task<(T Session, SteamSessionCredentials? Credentials)>> connect, CancellationToken token)
    {
        await _logon.WaitAsync(token).ConfigureAwait(false);
        try
        {
            SteamSessionCredentials? credentials;
            long generation;
            lock (_state) { credentials = _credentials; generation = _generation; }
            (T Session, SteamSessionCredentials? Credentials) result;
            try { result = await connect(credentials, token).ConfigureAwait(false); }
            catch (SteamSessionRejectedException) when (credentials is not null)
            {
                // 只有 Steam 明确拒绝已缓存的登录令牌，才要求重新扫码；网络失败保留授权。
                lock (_state) { if (_generation == generation) _credentials = null; }
                token.ThrowIfCancellationRequested();
                result = await connect(null, token).ConfigureAwait(false);
            }
            lock (_state)
                if (_generation == generation && result.Credentials is not null) _credentials = result.Credentials;
            return result.Session;
        }
        finally { _logon.Release(); }
    }

    public void Clear()
    {
        lock (_state) { _generation++; _credentials = null; }
    }
}

internal sealed class SteamSessionCredentials(string accountName, string refreshToken)
{
    internal string AccountName { get; } = accountName;
    internal string RefreshToken { get; } = refreshToken;
    public override string ToString() => "[Steam download authorization]";
}

internal sealed class SteamSessionRejectedException : IOException
{
    internal SteamSessionRejectedException(string result) : base($"Steam 下载授权已失效（{result}）。") { }
}
