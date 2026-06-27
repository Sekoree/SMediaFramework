using System.Buffers.Binary;
using OSCLib;

namespace S.Control;

public sealed class X32Session : IAsyncDisposable, IDisposable
{
    private readonly X32EndpointPreset _endpoint;
    private readonly IControlOscSender _sender;
    private readonly List<X32Subscription> _subscriptions = new();
    private readonly List<X32MeterSubscription> _meterSubscriptions = new();
    private CancellationTokenSource? _renewCts;
    private Task? _renewTask;
    private bool _disposed;

    public X32Session(X32EndpointPreset endpoint, IControlOscSender sender)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        Health = ControlSessionHealth.Stopped();
    }

    public ControlSessionHealth Health { get; private set; }

    public bool IsRunning => _renewTask is { IsCompleted: false };

    public void AddSubscription(string address, int frequency = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _subscriptions.Add(new X32Subscription(address, frequency));
    }

    public void AddMeterSubscription(string meterId, int? argument = null, int priority = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterId);
        _meterSubscriptions.Add(new X32MeterSubscription(meterId, argument, priority));
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return Task.CompletedTask;

        _renewCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renewTask = Task.Run(() => RenewLoopAsync(_renewCts.Token), CancellationToken.None);
        Health = ControlSessionHealth.Running($"X32 {_endpoint.Host}:{_endpoint.Port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var task = _renewTask;
        var cts = _renewCts;
        if (task is null)
            return;

        cts?.Cancel();
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts?.Dispose();
            _renewCts = null;
            _renewTask = null;
            Health = ControlSessionHealth.Stopped();
        }
    }

    public async Task RenewOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await SendXRemoteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var subscription in _subscriptions)
                await SendSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false);
            foreach (var meter in _meterSubscriptions)
                await SendMeterSubscriptionAsync(meter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Health = ControlSessionHealth.Faulted(ex.Message);
            throw;
        }
    }

    private async Task RenewLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RenewOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = MinPositive(_endpoint.XRemoteRenewInterval, _endpoint.SubscriptionRenewInterval);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendXRemoteAsync(CancellationToken cancellationToken) =>
        _sender.SendAsync(_endpoint.Host, _endpoint.Port, "/xremote", [], cancellationToken).AsTask();

    private Task SendSubscriptionAsync(X32Subscription subscription, CancellationToken cancellationToken) =>
        _sender.SendAsync(
            _endpoint.Host,
            _endpoint.Port,
            "/subscribe",
            [OSCArgument.String(subscription.Address), OSCArgument.Int32(subscription.Frequency)],
            cancellationToken).AsTask();

    private Task SendMeterSubscriptionAsync(X32MeterSubscription subscription, CancellationToken cancellationToken)
    {
        var args = subscription.Argument is { } arg
            ? new[] { OSCArgument.String(subscription.MeterId), OSCArgument.Int32(arg), OSCArgument.Int32(subscription.Priority) }
            : [OSCArgument.String(subscription.MeterId), OSCArgument.Int32(subscription.Priority)];
        return _sender.SendAsync(_endpoint.Host, _endpoint.Port, "/meters", args, cancellationToken).AsTask();
    }

    private static TimeSpan MinPositive(TimeSpan a, TimeSpan b)
    {
        if (a <= TimeSpan.Zero)
            return b > TimeSpan.Zero ? b : TimeSpan.FromSeconds(8);
        if (b <= TimeSpan.Zero)
            return a;
        return a <= b ? a : b;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renewCts?.Cancel();
        _renewCts?.Dispose();
        _renewCts = null;
        _renewTask = null;
        Health = ControlSessionHealth.Stopped();
    }
}

public sealed record X32Subscription(string Address, int Frequency = 50);

public sealed record X32MeterSubscription(string MeterId, int? Argument = null, int Priority = 1);

public sealed record X32MeterBlob(int Header0, int Header1, IReadOnlyList<float> Values);

public static class X32Meters
{
    public static X32MeterBlob ParseFloatBlob(ReadOnlyMemory<byte> blob)
    {
        var span = blob.Span;
        if (span.Length < 8 || (span.Length - 8) % 4 != 0)
            throw new FormatException("X32 float meter blob must contain two 32-bit headers followed by 32-bit float values.");

        var header0 = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
        var header1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
        var values = new float[(span.Length - 8) / 4];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8 + i * 4, 4));
            values[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return new X32MeterBlob(header0, header1, values);
    }

    public static IReadOnlyList<float> ParseRtaDbBlob(ReadOnlyMemory<byte> blob)
    {
        var span = blob.Span;
        if (span.Length % 2 != 0)
            throw new FormatException("X32 RTA meter blob must contain little-endian 16-bit values.");

        var values = new float[span.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            var raw = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
            values[i] = raw / 256.0f;
        }

        return values;
    }
}
