using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSCLib;

public sealed class OSCServer : IOSCServer
{
    private readonly UdpClient _udpClient;
    private readonly ILogger<OSCServer> _logger;
    private readonly OSCRouter _router = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTimeOffset _lastOversizeLogUtc = DateTimeOffset.MinValue;
    private long _oversizeDrops;
    // Future-dated bundle dispatch runs off-loop; the set tracks the in-flight tasks so
    // StopAsync/Dispose can drain them, the count enforces Options.MaxPendingScheduledBundles.
    private readonly ConcurrentDictionary<Task, byte> _pendingScheduled = new();
    private int _pendingScheduledCount;
    private long _scheduledBundleDrops;
    private DateTimeOffset _lastScheduledDropLogUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public OSCServer(OSCServerOptions options, ILogger<OSCServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port < IPEndPoint.MinPort || options.Port > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(options.Port), options.Port, "Port must be between 0 and 65535.");
        if (options.MaxPacketBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPacketBytes), options.MaxPacketBytes, "MaxPacketBytes must be greater than 0.");
        if (options.OversizeLogInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.OversizeLogInterval), options.OversizeLogInterval, "OversizeLogInterval cannot be negative.");

        Options = options;
        _logger = logger ?? NullLogger<OSCServer>.Instance;
        _udpClient = new UdpClient(options.Port);

        if (options.MulticastGroup != null)
            _udpClient.JoinMulticastGroup(
                options.MulticastGroup,
                options.MulticastLocalAddress ?? IPAddress.Any);
    }

    public OSCServerOptions Options { get; }

    /// <summary>Actual bound UDP port (differs from <see cref="OSCServerOptions.Port"/> when 0 = ephemeral). Test seam.</summary>
    internal int BoundPort => ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;

    /// <summary>Number of oversized packets dropped since construction (or last <see cref="ResetOversizeDropCount"/>).</summary>
    public long OversizeDropCount => Interlocked.Read(ref _oversizeDrops);

    /// <summary>Resets the oversize-drop counter to zero.</summary>
    public void ResetOversizeDropCount() => Interlocked.Exchange(ref _oversizeDrops, 0);

    /// <summary>Number of future-dated bundles dropped because <see cref="OSCServerOptions.MaxPendingScheduledBundles"/>
    /// dispatches were already pending (since construction or last <see cref="ResetScheduledBundleDropCount"/>).</summary>
    public long ScheduledBundleDropCount => Interlocked.Read(ref _scheduledBundleDrops);

    /// <summary>Resets the scheduled-bundle drop counter to zero.</summary>
    public void ResetScheduledBundleDropCount() => Interlocked.Exchange(ref _scheduledBundleDrops, 0);

    /// <summary>Future-dated bundles currently pending off-loop dispatch (diagnostic).</summary>
    public int PendingScheduledBundleCount => Volatile.Read(ref _pendingScheduledCount);

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public IDisposable RegisterHandler(string addressPattern, OSCMessageHandler handler)
        => _router.Register(addressPattern, handler);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return Task.CompletedTask;

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => ReceiveLoopAsync(_loopCts.Token), CancellationToken.None);
        _logger.LogInformation("OSC UDP server started on port {Port}", Options.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var loopTask = _loopTask;
        var loopCts = _loopCts;
        if (loopTask is null)
        {
            // Loop already stopped (or never started) - still drain any scheduled bundles a previous
            // caller-cancelled stop left behind.
            await WaitForScheduledBundlesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        loopCts?.Cancel();
        try
        {
            await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OSC receive loop exited with an exception during stop.");
        }
        finally
        {
            loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        await WaitForScheduledBundlesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("OSC UDP server stopped on port {Port}", Options.Port);
    }

    /// <summary>Drains the tracked future-bundle dispatch tasks. They observe the loop CTS (cancelled
    /// by the caller before this runs), so this only lingers for handlers already past their time tag.
    /// The tasks never fault - <see cref="DispatchScheduledBundleAsync"/> swallows and logs.</summary>
    private async Task WaitForScheduledBundlesAsync(CancellationToken cancellationToken)
    {
        if (_pendingScheduled.IsEmpty)
            return;

        try
        {
            await Task.WhenAll(_pendingScheduled.Keys).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller gave up waiting; the tasks still drain on their own via the loop CTS.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OSC server socket receive failed.");
                continue;
            }

            // Capture the receive timestamp immediately - before any decoding or dispatch -
            // to give handlers the most accurate network arrival time.
            var receivedAt = DateTimeOffset.UtcNow;

            if (received.Buffer.Length > Options.MaxPacketBytes)
            {
                HandleOversizePacket(received.Buffer.Length);
                continue;
            }

            if (Options.EnableTraceHexDump && _logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("OSC datagram {Length}B from {Remote}: {Hex}", received.Buffer.Length, received.RemoteEndPoint, Convert.ToHexString(received.Buffer));

            if (!OSCPacketCodec.TryDecode(received.Buffer, Options.DecodeOptions, out var packet, out var error))
            {
                _logger.LogWarning("Failed to decode OSC packet from {Remote}: {Error}", received.RemoteEndPoint, error);
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Decoded OSC packet {Kind} from {Remote}", packet!.Kind, received.RemoteEndPoint);

            // Head-of-line fix (perf review): a future-dated bundle used to be awaited INLINE,
            // stalling this loop - and therefore every subsequent datagram - until its time tag
            // (a bundle stamped 2 s ahead froze all OSC input for 2 s). Future bundles now
            // schedule off-loop; due packets keep the existing in-order serial dispatch.
            if (packet!.Kind == OSCPacketKind.Bundle
                && !Options.IgnoreTimeTagScheduling
                && OSCBundleScheduler.GetDelay(packet.Bundle!.TimeTag, DateTimeOffset.UtcNow) > TimeSpan.Zero)
            {
                ScheduleFutureBundle(packet, received.RemoteEndPoint, receivedAt, cancellationToken);
                continue;
            }

            await DispatchPacketAsync(packet!, received.RemoteEndPoint, null, receivedAt, cancellationToken, depth: 0).ConfigureAwait(false);
        }
    }

    /// <summary>Off-loop entry for a future-dated bundle: capped at
    /// <see cref="OSCServerOptions.MaxPendingScheduledBundles"/> so a flood of far-future time tags
    /// cannot pin unbounded tasks/packets (past the cap the bundle is dropped and counted, mirroring
    /// the oversize-drop pattern), and tracked so StopAsync/Dispose do not return while one is still
    /// dispatching. Only the receive loop calls this, so check-then-increment on the pending count is safe.</summary>
    private void ScheduleFutureBundle(OSCPacket packet, IPEndPoint remote, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _pendingScheduledCount) >= Options.MaxPendingScheduledBundles)
        {
            HandleScheduledBundleOverflow(remote);
            return;
        }

        Interlocked.Increment(ref _pendingScheduledCount);
        var task = DispatchScheduledBundleAsync(packet, remote, receivedAt, cancellationToken);
        _pendingScheduled.TryAdd(task, 0);
        // Untrack on completion (the task may already be complete if the clock passed the time tag).
        // The continuation - not the task body - removes it so the drain in StopAsync/Dispose never
        // misses a task that is mid-completion.
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var self = (OSCServer)state!;
                self._pendingScheduled.TryRemove(t, out _);
                Interlocked.Decrement(ref self._pendingScheduledCount);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleScheduledBundleOverflow(IPEndPoint remote)
    {
        Interlocked.Increment(ref _scheduledBundleDrops);

        var now = DateTimeOffset.UtcNow;
        if (now - _lastScheduledDropLogUtc < Options.OversizeLogInterval)
            return;

        _lastScheduledDropLogUtc = now;
        _logger.LogWarning(
            "Dropped future-dated OSC bundle from {Remote}: {Pending} scheduled bundles already pending (max {Max}). Total dropped: {DroppedCount}",
            remote,
            Volatile.Read(ref _pendingScheduledCount),
            Options.MaxPendingScheduledBundles,
            Interlocked.Read(ref _scheduledBundleDrops));
    }

    /// <summary>Background dispatch for a future-dated bundle: <see cref="DispatchPacketAsync"/>
    /// performs the time-tag delay itself; shutdown cancels the pending delay via the token.
    /// Handler faults are logged here - there is no caller left to observe them.</summary>
    private async Task DispatchScheduledBundleAsync(
        OSCPacket packet,
        IPEndPoint remote,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await DispatchPacketAsync(packet, remote, null, receivedAt, cancellationToken, depth: 0).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server stopping - the scheduled bundle is intentionally dropped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled OSC bundle dispatch failed (from {Remote}).", remote);
        }
    }

    /// <summary>Maximum nesting depth for OSC bundles. Prevents stack overflow from malicious payloads.</summary>
    private const int MaxBundleDepth = 32;

    private async Task DispatchPacketAsync(
        OSCPacket packet,
        IPEndPoint remote,
        OSCTimeTag? bundleTimeTag,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken,
        int depth)
    {
        if (packet.Kind == OSCPacketKind.Message)
        {
            var context = new OSCMessageContext(packet.Message!, remote, bundleTimeTag, receivedAt);
            _ = await _router.DispatchAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (depth >= MaxBundleDepth)
        {
            _logger.LogWarning("OSC bundle nesting depth {Depth} exceeds limit {Max} - dropping bundle from {Remote}.", depth, MaxBundleDepth, remote);
            return;
        }

        var bundle = packet.Bundle!;
        if (!Options.IgnoreTimeTagScheduling)
            await OSCBundleScheduler.DelayUntilDueAsync(bundle.TimeTag, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

        foreach (var child in bundle.Elements)
            await DispatchPacketAsync(child, remote, bundle.TimeTag, receivedAt, cancellationToken, depth + 1).ConfigureAwait(false);
    }

    private void HandleOversizePacket(int packetLength)
    {
        Interlocked.Increment(ref _oversizeDrops);

        // P2.9: Previously, OversizePolicy.Throw would throw inside the receive loop,
        // killing it entirely. Now we always log and drop; the Throw policy escalates
        // to Error-level logging so monitoring can alert.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastOversizeLogUtc < Options.OversizeLogInterval)
            return;

        _lastOversizeLogUtc = now;

        if (Options.OversizePolicy == OSCOversizePolicy.Throw)
        {
            _logger.LogError(
                "OSC packet size {Length}B exceeds configured max {MaxPacketBytes}B (OversizePolicy=Throw). Total dropped: {DroppedCount}",
                packetLength,
                Options.MaxPacketBytes,
                _oversizeDrops);
        }
        else
        {
            _logger.LogWarning(
                "Dropped oversized OSC datagram {Length}B (> {MaxPacketBytes}B). Total dropped: {DroppedCount}",
                packetLength,
                Options.MaxPacketBytes,
                _oversizeDrops);
        }
    }

    /// <remarks>
    /// Cooperative task shutdown uses sliced <c>Wait</c> calls - intentional duplication versus the media stack so
    /// OSCLib stays free of any <c>S.Media.Core</c> dependency for thread joins.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _loopCts?.Cancel();
            var deadlineTicks = Environment.TickCount64 + 2000;
            WaitBounded(_loopTask, deadlineTicks);
            // Scheduled future-bundle dispatches observe the loop CTS cancelled above; give them the
            // remainder of the same deadline instead of abandoning them mid-dispatch.
            WaitBounded(_pendingScheduled.IsEmpty ? null : Task.WhenAll(_pendingScheduled.Keys), deadlineTicks);
        }
        catch (Exception ex)
        {
#if DEBUG
            _logger.LogDebug(ex, "OSCServer.Dispose: cooperative shutdown join/cancel (best effort).");
#else
            _ = ex;
#endif
        }
        finally
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        _udpClient.Dispose();
        _disposed = true;
    }

    private static void WaitBounded(Task? task, long deadlineTicks)
    {
        if (task is null)
            return;

        while (!task.IsCompleted)
        {
            var remainMs = deadlineTicks - Environment.TickCount64;
            if (remainMs <= 0)
                break;

            var slice = remainMs > 32 ? 32 : (int)remainMs;
            if (slice < 1) slice = 1;
            task.Wait(TimeSpan.FromMilliseconds(slice));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
#if DEBUG
            _logger.LogDebug(ex, "OSCServer.DisposeAsync: StopAsync (best effort).");
#else
            _ = ex;
#endif
        }

        _udpClient.Dispose();
        _disposed = true;
    }
}
