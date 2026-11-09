using static Ck3ModManager.Core.AppText;
using System.Collections.ObjectModel;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

/// <summary>每个游戏拥有独立工作区；切换只更换窗口上下文，不重用其他游戏的状态。</summary>
public sealed class GameWorkspaceManager : IDisposable
{
    private sealed class Session(MainWindowViewModel workspace)
    {
        public MainWindowViewModel Workspace { get; } = workspace;
        public Task? Initialization { get; set; }
    }

    private readonly Func<GameDefinition, MainWindowViewModel> _factory;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ObservableCollection<GameDefinition> _games = new(GameCatalog.All);
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<GameDefinition>>>? _discoverGames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _discoveryGate = new(1);
    private string? _steamDirectory;
    private bool _disposed;

    public GameWorkspaceManager(Func<GameDefinition, MainWindowViewModel> factory, GameDefinition? initialGame = null,
        Func<string?, CancellationToken, Task<IReadOnlyList<GameDefinition>>>? discoverGames = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _discoverGames = discoverGames;
        Current = GetSession(GameCatalog.Get((initialGame ?? GameCatalog.Ck3).Id)).Workspace;
    }

    public MainWindowViewModel Current { get; private set; }
    public event Action<MainWindowViewModel>? WorkspaceChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 启动即刷新 Steam 游戏目录，不等待本地 Mod 初始化完成。
        var refresh = RefreshGamesAsync(null, false, cancellationToken);
        var session = GetSession(Current.Game);
        await InitializeSession(session, cancellationToken);
        await refresh;
    }

    private async Task RefreshGamesAsync(string? steamDirectory, bool replaceSteamDirectory, CancellationToken cancellationToken)
    {
        if (_disposed || _discoverGames is null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = operation.Token;
        try
        {
            await _discoveryGate.WaitAsync(token);
            try
            {
                if (steamDirectory is not null || replaceSteamDirectory) _steamDirectory = steamDirectory;
                var games = await _discoverGames(_steamDirectory, token);
                token.ThrowIfCancellationRequested();
                var ids = games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
                for (var index = _games.Count - 1; index >= 0; index--)
                    if (_games[index].IsDiscovered && _games[index].Id != Current.Game.Id && !ids.Contains(_games[index].Id))
                        _games.RemoveAt(index);
                foreach (var game in games.Where(game => game.IsDiscovered))
                {
                    var existing = _games.FirstOrDefault(item => item.Id == game.Id);
                    if (existing is null) _games.Add(game);
                    else if (existing.HasSteamWorkshop != game.HasSteamWorkshop ||
                        existing.DisplayName != game.DisplayName || existing.SteamLibraryHint != game.SteamLibraryHint ||
                        !existing.SteamLibraryRoots.SequenceEqual(game.SteamLibraryRoots, StringComparer.Ordinal))
                        _games[_games.IndexOf(existing)] = game;
                }
                Current.UpdateGameSelection();
            }
            finally { _discoveryGate.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
    }

    public Task SelectGameAsync(GameDefinition game, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        var knownGame = _games.FirstOrDefault(candidate => candidate.Id == game.Id)
            ?? throw new ArgumentException(T("UnsupportedGame", game.Id), nameof(game));
        var session = GetSession(knownGame);
        if (!ReferenceEquals(Current, session.Workspace))
        {
            Current.SetLocalModMonitoringEnabled(false);
            Current = session.Workspace;
            Current.SetLocalModMonitoringEnabled(true);
            WorkspaceChanged?.Invoke(Current);
        }
        return InitializeSession(session, cancellationToken);
    }

    private Session GetSession(GameDefinition game)
    {
        if (_sessions.TryGetValue(game.Id, out var session)) return session;
        var workspace = _factory(game);
        if (workspace.Game.Id != game.Id)
        {
            workspace.Dispose();
            throw new InvalidOperationException(T("TheWorkspaceFactoryReturnedAWorkspaceFor"));
        }
        workspace.AttachGameCatalog(_games, RefreshGamesAsync);
        workspace.GameChangeRequested += OnGameChangeRequested;
        session = new Session(workspace);
        _sessions.Add(game.Id, session);
        return session;
    }

    private static Task InitializeSession(Session session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Initialization ??= session.Workspace.InitializeAsync(cancellationToken);
    }

    private void OnGameChangeRequested(GameDefinition game)
    {
        if (!_disposed) _ = SelectGameAsync(game);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Workspace.GameChangeRequested -= OnGameChangeRequested;
            session.Workspace.Dispose();
        }
        _sessions.Clear();
        WorkspaceChanged = null;
    }
}
