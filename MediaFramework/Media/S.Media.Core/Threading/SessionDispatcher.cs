using System.Collections.Concurrent;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Threading;

/// <summary>
/// Serial command loop for the public session API (D5 / OQ8). Callers <see cref="Post"/>
/// (fire-and-forget) or <see cref="InvokeAsync(Action)"/> (awaitable) onto one owner context; queries
/// elsewhere read immutable snapshots. There is deliberately <strong>no</strong> blocking
/// <c>Invoke</c> - that is the deadlock OQ8 warns about - so a UI/plugin callback can only re-enter via
/// <c>Post</c>/<c>InvokeAsync</c>, never by blocking the loop on itself. Lives in Core so every tier
/// shares one dispatcher contract; <c>S.Media.Session</c>'s <c>ShowSession</c> owns one (Phase 4).
/// </summary>
public sealed class SessionDispatcher : IDisposable, IAsyncDisposable
{
    private static readonly AsyncLocal<SessionDispatcher?> Current = new();

    private readonly BlockingCollection<Func<Task>> _queue = new(new ConcurrentQueue<Func<Task>>());
    private readonly Task _pump;
    private volatile bool _disposed;

    public SessionDispatcher(string? name = null)
    {
        _pump = Task.Run(RunLoopAsync);
    }

    /// <summary>True when the calling code is running on this dispatcher's logical owner context.</summary>
    public bool IsOnDispatcherThread => ReferenceEquals(Current.Value, this);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the loop. Returns <c>true</c> if enqueued; <c>false</c>
    /// if the dispatcher is disposed (or disposing) so the work won't run - callers that await completion
    /// must handle that (see <see cref="InvokeAsync(Action)"/>, which faults instead of hanging).
    /// </summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return PostWork(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>Queues <paramref name="action"/> and completes the returned task when it has run (or faulted).</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task with its result (or fault).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeAsync(() => Task.FromResult(func()));
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task when the async work has run (or faulted).</summary>
    public Task InvokeAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeAsync(async () =>
        {
            await func().ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task with its async result (or fault).</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsOnDispatcherThread)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PostWork(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            // Enqueue failed (disposed / CompleteAdding race): fault so the awaiter never hangs.
            tcs.TrySetException(new ObjectDisposedException(nameof(SessionDispatcher)));
        }

        return tcs.Task;
    }

    private bool PostWork(Func<Task> work)
    {
        if (_disposed)
            return false;
        try
        {
            _queue.Add(work);
            return true;
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with Post during shutdown.
            return false;
        }
    }

    private async Task RunLoopAsync()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            var previous = Current.Value;
            Current.Value = this;
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning("SessionDispatcher: queued work threw: {0}", ex.Message);
            }
            finally
            {
                Current.Value = previous;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (IsOnDispatcherThread)
            return;

        try
        {
            await _pump.ConfigureAwait(false);
        }
        finally
        {
            _queue.Dispose();
        }
    }
}
