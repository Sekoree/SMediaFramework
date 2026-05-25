using S.Media.Core.Triggers;

namespace OSCLib;

/// <summary>Maps inbound OSC messages to <see cref="TriggerBus.Fire"/> using the message address as the trigger id.</summary>
public sealed class OscTriggerBridge : IDisposable
{
    private readonly TriggerBus _bus;
    private readonly IOSCServer _server;
    private readonly IDisposable _registration;
    private bool _disposed;

    public OscTriggerBridge(TriggerBus bus, IOSCServer server)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _registration = _server.RegisterHandler("//", OnMessageAsync);
    }

    private ValueTask OnMessageAsync(OSCMessageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = MapMessageToPayload(context.Message);
        _bus.Fire(context.Message.Address, payload);
        return ValueTask.CompletedTask;
    }

    /// <summary>Maps the first OSC argument to a <see cref="TriggerPayload"/> (shared with tests).</summary>
    public static TriggerPayload MapMessageToPayload(OSCMessage message)
    {
        if (message.Arguments.Count == 0)
            return TriggerPayload.None;

        var first = message.Arguments[0];
        return first.Type switch
        {
            OSCArgumentType.Float32 => TriggerPayload.FromNumeric(first.AsFloat32()),
            OSCArgumentType.Double64 => TriggerPayload.FromNumeric(first.AsDouble64()),
            OSCArgumentType.Int32 => TriggerPayload.FromNumeric(first.AsInt32()),
            OSCArgumentType.Int64 => TriggerPayload.FromNumeric(first.AsInt64()),
            OSCArgumentType.String or OSCArgumentType.Symbol => TriggerPayload.FromText(first.AsString()),
            OSCArgumentType.True => TriggerPayload.FromNumeric(1),
            OSCArgumentType.False => TriggerPayload.FromNumeric(0),
            OSCArgumentType.Impulse => TriggerPayload.FromNumeric(1),
            _ => TriggerPayload.None,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.Dispose();
    }
}
