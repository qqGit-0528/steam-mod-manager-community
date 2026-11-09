namespace Ck3ModManager.Application;

/// <summary>每名调用方只取消自己的等待；最后一名离开时才取消来源请求。</summary>
internal sealed class SharedWorkshopRequests
{
    private sealed class Flight<T>
    {
        public readonly CancellationTokenSource Cancellation = new();
        public int Users;
        public Lazy<Task<T>> Task { get; }
        public Flight(Func<CancellationToken, Task<T>> fetch) =>
            Task = new(async () => await fetch(Cancellation.Token).ConfigureAwait(false));
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, object> _flights = [];

    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        Flight<T> flight;
        lock (_gate)
        {
            if (_flights.TryGetValue(key, out var existing)) flight = (Flight<T>)existing;
            else _flights[key] = flight = new Flight<T>(fetch);
            flight.Users++;
        }
        try { return await flight.Task.Value.WaitAsync(caller).ConfigureAwait(false); }
        finally
        {
            bool last;
            lock (_gate)
            {
                last = --flight.Users == 0;
                if (last && _flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            }
            if (last)
            {
                if (!flight.Task.Value.IsCompleted) flight.Cancellation.Cancel();
                _ = flight.Task.Value.ContinueWith(_ => flight.Cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
