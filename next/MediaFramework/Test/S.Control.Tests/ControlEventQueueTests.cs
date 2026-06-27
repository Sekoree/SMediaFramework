using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlEventQueueTests
{
    [Fact]
    public async Task DispatchControlEventAsync_RunsQueuedEventsInFifoOrder()
    {
        var midiId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var sender = new BlockingOscSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MidiDevice(midiId),
                OscDevice(x32Id, "x32"),
            ],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "MIDI",
                    ScriptPath = "Scripts/midi.mnd",
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.MidiControlChange,
                            FunctionName = "onMidi",
                            DeviceInstanceId = midiId,
                        },
                    ],
                },
            ],
        };
        var session = new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/midi.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        osc.send("x32", "/seen", osc.int32(event.midi.controller));
                    }
                    """,
            }),
            sender);
        await using var queue = new ControlEventQueue(session);

        var first = queue.DispatchControlEventAsync(MidiCcEvent(midiId, controller: 16, value: 1)).AsTask();
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = queue.DispatchControlEventAsync(MidiCcEvent(midiId, controller: 17, value: 2)).AsTask();

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        sender.ReleaseFirstSend.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, queue.PendingCount);
        Assert.Collection(
            sender.Sent,
            sent => Assert.Equal(16, Assert.Single(sent.Arguments).AsInt32()),
            sent => Assert.Equal(17, Assert.Single(sent.Arguments).AsInt32()));
    }

    private static ControlDeviceInstanceConfig MidiDevice(Guid id) =>
        new()
        {
            Id = id,
            Name = "X-Touch Mini",
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
        };

    private static ControlDeviceInstanceConfig OscDevice(Guid id, string alias) =>
        new()
        {
            Id = id,
            Name = "X32",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OscHost = "127.0.0.1",
                OscPort = 10023,
            },
        };

    private static MidiControlEvent MidiCcEvent(Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

    private sealed class BlockingOscSender : IControlOscSender
    {
        private readonly object _gate = new();
        private readonly List<SentOscMessage> _sent = new();
        private int _sendCount;

        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<SentOscMessage> Sent
        {
            get
            {
                lock (_gate)
                    return _sent.ToArray();
            }
        }

        public async ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _sendCount) == 1)
            {
                FirstSendStarted.SetResult();
                await ReleaseFirstSend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
                _sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
        }
    }

    private sealed record SentOscMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
