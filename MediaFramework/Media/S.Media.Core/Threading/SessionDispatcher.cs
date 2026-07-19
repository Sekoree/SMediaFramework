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
    public const int DefaultCapacity = 4096;

    private static readonly AsyncLocal<SessionDispatcher?> Current = new();

    private readonly BlockingCollection<Func<Task>> _queue;
    private readonly object _metricsGate = new();
    private readonly Task _pump;
    private int _disposeState;
    private int _queueDisposed;
    private int _queuedWorkCount;
    private int _highWaterMark;
    private long _rejectedWorkCount;

    public SessionDispatcher(string? name = null, int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be >= 1");

        Name = string.IsNullOrWhiteSpace(name) ? nameof(SessionDispatcher) : name;
        Capacity = capacity;
        _queue = new BlockingCollection<Func<Task>>(new ConcurrentQueue<Func<Task>>(), capacity);
        _pump = Task.Run(RunLoopAsync);
    }

    public string Name { get; }

    public int Capacity { get; }

    /// <summary>Work waiting in the bounded queue; excludes the command currently executing.</summary>
    public int QueuedWorkCount => Math.Max(0, Volatile.Read(ref _queuedWorkCount));

    public int HighWaterMark => Volatile.Read(ref _highWaterMark);

    public long RejectedWorkCount => Interlocked.Read(ref _rejectedWorkCount);

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>Atomic-enough point-in-time queue health for host telemetry. Individual fields may advance
    /// while this value is assembled; all counters are monotonic except current depth.</summary>
    public SessionDispatcherDiagnostics Diagnostics => new(
        Name,
        Capacity,
        QueuedWorkCount,
        HighWaterMark,
        RejectedWorkCount,
        IsDisposed);

    /// <summary>True when the calling code is running on this dispatcher's logical owner context.</summary>
    public bool IsOnDispatcherThread => ReferenceEquals(Current.Value, this);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the loop. Returns <c>true</c> if enqueued; <c>false</c>
    /// if the bounded queue is full or the dispatcher is disposed/disposing, so the work won't run. Callers
    /// that await completion must handle that (see <see cref="InvokeAsync(Action)"/>, which faults instead of hanging).
    /// </summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryPostWork(() =>
        {
            action();
            return Task.CompletedTask;
        }) == EnqueueResult.Accepted;
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
        var enqueue = TryPostWork(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        if (enqueue != EnqueueResult.Accepted)
        {
            // Enqueue failed: fault so the awaiter never hangs and distinguish shutdown from overload.
            tcs.TrySetException(enqueue == EnqueueResult.Full
                ? new SessionDispatcherOverloadedException(Name, Capacity)
                : new ObjectDisposedException(nameof(SessionDispatcher)));
        }

        return tcs.Task;
    }

    private EnqueueResult TryPostWork(Func<Task> work)
    {
        if (IsDisposed)
            return EnqueueResult.Disposed;
        try
        {
            lock (_metricsGate)
            {
                if (!_queue.TryAdd(work))
                {
                    if (IsDisposed || _queue.IsAddingCompleted)
                        return EnqueueResult.Disposed;
                    Interlocked.Increment(ref _rejectedWorkCount);
                    return EnqueueResult.Full;
                }

                _queuedWorkCount++;
                if (_queuedWorkCount > _highWaterMark)
                    _highWaterMark = _queuedWorkCount;
            }
            return EnqueueResult.Accepted;
        }
        catch (ObjectDisposedException)
        {
            return EnqueueResult.Disposed;
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with Post during shutdown.
            return EnqueueResult.Disposed;
        }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                lock (_metricsGate)
                    _queuedWorkCount--;
                var previous = Current.Value;
                Current.Value = this;
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogWarning("SessionDispatcher '{0}': queued work threw: {1}", Name, ex.Message);
                }
                finally
                {
                    Current.Value = previous;
                }
            }
        }
        finally
        {
            DisposeQueueOnce();
        }
    }

    public void Dispose()
    {
        CompleteAddingOnce();
        if (IsOnDispatcherThread)
            return;

        _pump.GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        CompleteAddingOnce();
        if (IsOnDispatcherThread)
            return;

        await _pump.ConfigureAwait(false);
    }

    private void CompleteAddingOnce()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _queue.CompleteAdding();
    }

    private void DisposeQueueOnce()
    {
        if (Interlocked.Exchange(ref _queueDisposed, 1) == 0)
        {
            _queue.Dispose();
        }
    }

    private enum EnqueueResult
    {
        Accepted,
        Full,
        Disposed,
    }
}

public readonly record struct SessionDispatcherDiagnostics(
    string Name,
    int Capacity,
    int QueuedWorkCount,
    int HighWaterMark,
    long RejectedWorkCount,
    bool IsDisposed);

/// <summary>Raised by awaited dispatcher calls when the configured pending-work capacity is exhausted.</summary>
public sealed class SessionDispatcherOverloadedException : InvalidOperationException
{
    public SessionDispatcherOverloadedException(string dispatcherName, int capacity)
        : base($"Session dispatcher '{dispatcherName}' is full (capacity {capacity}).")
    {
        DispatcherName = dispatcherName;
        Capacity = capacity;
    }

    public string DispatcherName { get; }

    public int Capacity { get; }
}
