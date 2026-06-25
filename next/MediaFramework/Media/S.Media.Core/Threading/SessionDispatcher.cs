using System.Collections.Concurrent;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Threading;

/// <summary>
/// Single-threaded command loop for the public session API (D5 / OQ8). Callers <see cref="Post"/>
/// (fire-and-forget) or <see cref="InvokeAsync(Action)"/> (awaitable) onto one owner thread; queries
/// elsewhere read immutable snapshots. There is deliberately <strong>no</strong> blocking
/// <c>Invoke</c> — that is the deadlock OQ8 warns about — so a UI/plugin callback can only re-enter via
/// <c>Post</c>/<c>InvokeAsync</c>, never by blocking the loop on itself. Lives in Core so every tier
/// shares one dispatcher contract; <c>S.Media.Session</c>'s <c>ShowSession</c> owns one (Phase 4).
/// </summary>
public sealed class SessionDispatcher : IDisposable
{
    [ThreadStatic] private static SessionDispatcher? _current;

    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private volatile bool _disposed;

    public SessionDispatcher(string? name = null)
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = name ?? "session-dispatcher",
        };
        _thread.Start();
    }

    /// <summary>True when the calling code is running on this dispatcher's loop thread.</summary>
    public bool IsOnDispatcherThread => ReferenceEquals(_current, this);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the loop. Returns <c>true</c> if enqueued; <c>false</c>
    /// if the dispatcher is disposed (or disposing) so the work won't run — callers that await completion
    /// must handle that (see <see cref="InvokeAsync(Action)"/>, which faults instead of hanging).
    /// </summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return false;
        try
        {
            _queue.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with Post during shutdown.
            return false;
        }
    }

    /// <summary>Queues <paramref name="action"/> and completes the returned task when it has run (or faulted).</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
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

    /// <summary>Queues <paramref name="func"/> and completes the returned task with its result (or fault).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
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

    private void RunLoop()
    {
        _current = this;
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogWarning("SessionDispatcher: queued work threw: {0}", ex.Message);
                }
            }
        }
        finally
        {
            _current = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}
