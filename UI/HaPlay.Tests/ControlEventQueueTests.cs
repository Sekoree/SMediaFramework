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
        var sender = new BlockingOSCSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MIDIDevice(midiId),
                OSCDevice(x32Id, "x32"),
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
                            Kind = ControlScriptTriggerKind.MIDIControlChange,
                            FunctionName = "onMIDI",
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
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/seen", osc.int32(event.midi.controller));
                    }
                    """,
            }),
            sender);
        await using var queue = new ControlEventQueue(session);

        var first = queue.DispatchControlEventAsync(MIDICcEvent(midiId, controller: 16, value: 1)).AsTask();
        // Generous cap: the FIRST dispatch pays the Mond script cold-start (compile + JIT), which can exceed
        // a couple of seconds on a loaded CI runner. WaitAsync resolves the instant the send starts, so a big
        // cap costs nothing on success and only bites a genuine hang (the old 2s flaked → TimeoutException).
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = queue.DispatchControlEventAsync(MIDICcEvent(midiId, controller: 17, value: 2)).AsTask();

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        sender.ReleaseFirstSend.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, queue.PendingCount);
        Assert.Collection(
            sender.Sent,
            sent => Assert.Equal(16, Assert.Single(sent.Arguments).AsInt32()),
            sent => Assert.Equal(17, Assert.Single(sent.Arguments).AsInt32()));
    }

    private static ControlDeviceInstanceConfig MIDIDevice(Guid id) =>
        new()
        {
            Id = id,
            Name = "X-Touch Mini",
            Protocol = ControlDeviceProtocol.MIDI,
            IsEnabled = true,
        };

    private static ControlDeviceInstanceConfig OSCDevice(Guid id, string alias) =>
        new()
        {
            Id = id,
            Name = "X32",
            Protocol = ControlDeviceProtocol.OSC,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OSCHost = "127.0.0.1",
                OSCPort = 10023,
            },
        };

    private static MIDIControlEvent MIDICcEvent(Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

    private sealed class BlockingOSCSender : IControlOSCSender
    {
        private readonly object _gate = new();
        private readonly List<SentOSCMessage> _sent = new();
        private int _sendCount;

        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<SentOSCMessage> Sent
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
                _sent.Add(new SentOSCMessage(host, port, address, arguments.ToArray()));
        }
    }

    private sealed record SentOSCMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
