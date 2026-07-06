using System.Diagnostics;
using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlEventQueueTests
{
    [Fact]
    public async Task Queue_IsBounded_AndCoalescesFloodedContinuousControl()
    {
        // CTRL-01: while the worker is blocked on one event, flooding the SAME continuous control must
        // stay bounded and coalesce rather than grow memory/latency without limit.
        var midiId = Guid.NewGuid();
        var sourceNode = Guid.NewGuid(); // stable source → same controller coalesces
        var sender = new BlockingOSCSender();
        var session = BuildMidiToOscSession(midiId, sender);
        await using var queue = new ControlEventQueue(session, monitor: null, capacity: 8);

        var first = queue
            .DispatchControlEventAsync(MIDICcEvent(sourceNode, midiId, controller: 5, value: 0))
            .AsTask();
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flooded = new List<Task>();
        for (var v = 1; v <= 500; v++)
            flooded.Add(queue
                .DispatchControlEventAsync(MIDICcEvent(sourceNode, midiId, controller: 5, value: v))
                .AsTask());

        // Bounded: queued work never exceeds capacity plus the one in-flight item.
        Assert.True(queue.PendingCount <= 8 + 1, $"PendingCount={queue.PendingCount}");
        // A sustained flood of one control is absorbed by coalescing, not unbounded growth.
        Assert.True(queue.CoalescedCount > 0, "expected coalescing under pressure");

        sender.ReleaseFirstSend.SetResult();
        await Task.WhenAll(flooded).WaitAsync(TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsWithinBound_WhenScriptIgnoresCancellation()
    {
        // CTRL-02: a non-cooperative (token-ignoring) script must not block teardown forever.
        var midiId = Guid.NewGuid();
        var sender = new IgnoreCancellationOSCSender();
        var session = BuildMidiToOscSession(midiId, sender);
        var queue = new ControlEventQueue(
            session, monitor: null, capacity: 8, shutdownTimeout: TimeSpan.FromMilliseconds(250));

        try
        {
            _ = queue.DispatchControlEventAsync(MIDICcEvent(Guid.NewGuid(), midiId, 5, 1)).AsTask();
            await sender.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var sw = Stopwatch.StartNew();
            await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"DisposeAsync took {sw.Elapsed}");
        }
        finally
        {
            sender.Release.TrySetResult(); // let the abandoned worker finish so it doesn't linger
        }
    }

    private static ControlScriptRuntimeSession BuildMidiToOscSession(Guid midiId, IControlOSCSender sender)
    {
        var x32Id = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices = [MIDIDevice(midiId), OSCDevice(x32Id, "x32")],
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
        return new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/midi.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/seen", osc.int32(event.midi.value));
                    }
                    """,
            }),
            sender);
    }

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
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = queue.DispatchControlEventAsync(MIDICcEvent(midiId, controller: 17, value: 2)).AsTask();

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
        MIDICcEvent(Guid.NewGuid(), deviceId, controller, value);

    private static MIDIControlEvent MIDICcEvent(Guid sourceNodeId, Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            sourceNodeId,
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

    private sealed class IgnoreCancellationOSCSender : IControlOSCSender
    {
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            SendStarted.TrySetResult();
            await Release.Task.ConfigureAwait(false); // deliberately ignores cancellationToken
        }
    }

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
