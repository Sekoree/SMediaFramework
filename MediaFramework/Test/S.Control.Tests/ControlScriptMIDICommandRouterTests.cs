using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptMIDICommandRouterTests
{
    [Fact]
    public async Task SendAllAsync_RoutesMessagesToMultipleMIDIDevicesByAlias()
    {
        var xtouchId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var sender = new RecordingMIDISender();
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    MIDIDevice(xtouchId, "X-Touch Mini", "xtouch", "X-Touch MINI"),
                    MIDIDevice(backupId, "Backup Surface", "backup", "Backup MIDI Out"),
                ],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptMIDIMessage("xtouch", ControlScriptMIDIMessageKind.ControlChange, 1, Controller: 16, Value: 64),
            new ControlScriptMIDIMessage("xtouch", ControlScriptMIDIMessageKind.NoteOn, 1, Note: 89, Velocity: 127),
            new ControlScriptMIDIMessage("backup", ControlScriptMIDIMessageKind.ProgramChange, 2, Value: 5),
            new ControlScriptMIDIMessage("backup", ControlScriptMIDIMessageKind.PitchBend, 2, Value: 8192),
        ]);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Collection(
            sender.Sent,
            sent =>
            {
                Assert.Equal(ControlScriptMIDIMessageKind.ControlChange, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(1, sent.Channel);
                Assert.Equal(16, sent.Controller);
                Assert.Equal(64, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMIDIMessageKind.NoteOn, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(89, sent.Note);
                Assert.Equal(127, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMIDIMessageKind.ProgramChange, sent.Kind);
                Assert.Equal(backupId, sent.EndpointId);
                Assert.Equal(2, sent.Channel);
                Assert.Equal(5, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMIDIMessageKind.PitchBend, sent.Kind);
                Assert.Equal(backupId, sent.EndpointId);
                Assert.Equal(8192, sent.Value);
            });
    }

    [Fact]
    public async Task SendAsync_DoesNotRouteToDisabledMIDIDevice()
    {
        var sender = new RecordingMIDISender();
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices = [MIDIDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", "X-Touch MINI", isEnabled: false)],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMIDIMessage("xtouch", ControlScriptMIDIMessageKind.ControlChange, 1, Controller: 16, Value: 64));

        Assert.False(result.Succeeded);
        Assert.Contains("No enabled MIDI device matches key 'xtouch'", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsAmbiguousProfileKey()
    {
        var sender = new RecordingMIDISender();
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    MIDIDevice(Guid.NewGuid(), "X-Touch A", "xtouch-a", "X-Touch A", profileId: "behringer.xtouch-mini.mc"),
                    MIDIDevice(Guid.NewGuid(), "X-Touch B", "xtouch-b", "X-Touch B", profileId: "behringer.xtouch-mini.mc"),
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMIDIMessage("behringer.xtouch-mini.mc", ControlScriptMIDIMessageKind.ControlChange, 1, Controller: 16, Value: 64));

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsMIDIDeviceWithoutOutputBinding()
    {
        var sender = new RecordingMIDISender();
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "X-Touch Mini",
                        Protocol = ControlDeviceProtocol.MIDI,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig { Alias = "xtouch" },
                    },
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMIDIMessage("xtouch", ControlScriptMIDIMessageKind.NoteOff, 1, Note: 89));

        Assert.False(result.Succeeded);
        Assert.Contains("does not have an output binding", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_ReturnsFailureWhenSenderIsMissing()
    {
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices = [MIDIDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", "X-Touch MINI")],
            },
            sender: null);

        var result = await router.SendAsync(
            new ControlScriptMIDIMessage("xtouch", ControlScriptMIDIMessageKind.NoteOn, 1, Note: 89, Velocity: 127));

        Assert.False(result.Succeeded);
        Assert.Contains("No MIDI sender is configured", result.ErrorMessage);
    }

    [Fact]
    public async Task SendAllAsync_RoutesExtendedMIDIMessagesByAlias()
    {
        var deviceId = Guid.NewGuid();
        var sender = new RecordingMIDISender();
        var router = new ControlScriptMIDICommandRouter(
            new ControlSystemConfig
            {
                Devices = [MIDIDevice(deviceId, "Synth", "synth", "Synth Out")],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.PolyphonicAftertouch, 1, Note: 60, Value: 70),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.ChannelAftertouch, 1, Value: 71),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.SysEx, Data: [0xF0, 0x7D, 0x01, 0xF7]),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.MIDITimeCode, Value: 0x12),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.SongPosition, Value: 96),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.SongSelect, Value: 3),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.TuneRequest),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.TimingClock),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.Start),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.Continue),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.Stop),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.ActiveSensing),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.Reset),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.NRPN, 1, Parameter: 100, Value: 200),
            new ControlScriptMIDIMessage("synth", ControlScriptMIDIMessageKind.RPN, 1, Parameter: 101, Value: 201),
        ]);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(15, sender.Sent.Count);
        Assert.All(sender.Sent, sent => Assert.Equal(deviceId, sent.EndpointId));
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.PolyphonicAftertouch && sent.Note == 60 && sent.Value == 70);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.ChannelAftertouch && sent.Value == 71);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.SysEx && sent.Data?.SequenceEqual(new byte[] { 0xF0, 0x7D, 0x01, 0xF7 }) == true);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.MIDITimeCode && sent.Value == 0x12);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.SongPosition && sent.Value == 96);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.SongSelect && sent.Value == 3);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.TuneRequest);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.TimingClock);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.Start);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.Continue);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.Stop);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.ActiveSensing);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.Reset);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.NRPN && sent.Parameter == 100 && sent.Value == 200);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMIDIMessageKind.RPN && sent.Parameter == 101 && sent.Value == 201);
    }

    private static ControlDeviceInstanceConfig MIDIDevice(
        Guid id,
        string name,
        string alias,
        string outputName,
        bool isEnabled = true,
        string profileId = "generic-midi") =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = profileId,
            Protocol = ControlDeviceProtocol.MIDI,
            IsEnabled = isEnabled,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MIDIOutputDeviceName = outputName,
            },
        };

    private sealed class RecordingMIDISender : IControlMIDISender
    {
        public List<SentMIDIMessage> Sent { get; } = new();

        public ValueTask SendControlChangeAsync(
            Guid? endpointId,
            int channel,
            int controller,
            int value,
            bool highResolution14Bit,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMIDIMessage(
                ControlScriptMIDIMessageKind.ControlChange,
                endpointId,
                channel,
                Controller: controller,
                Note: null,
                Value: value,
                HighResolution14Bit: highResolution14Bit));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendNoteAsync(
            Guid? endpointId,
            int channel,
            int note,
            int velocity,
            bool isNoteOn,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMIDIMessage(
                isNoteOn ? ControlScriptMIDIMessageKind.NoteOn : ControlScriptMIDIMessageKind.NoteOff,
                endpointId,
                channel,
                Controller: null,
                Note: note,
                Value: velocity,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendProgramChangeAsync(
            Guid? endpointId,
            int channel,
            int program,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMIDIMessage(
                ControlScriptMIDIMessageKind.ProgramChange,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: program,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendPitchBendAsync(
            Guid? endpointId,
            int channel,
            int value,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMIDIMessage(
                ControlScriptMIDIMessageKind.PitchBend,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: value,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendMIDIMessageAsync(
            Guid? endpointId,
            ControlMIDIMessagePayload message,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMIDIMessage(
                ToScriptMessageKind(message.MessageType),
                endpointId,
                message.Channel ?? 0,
                Controller: message.Controller,
                Note: message.Note,
                Value: message.Value ?? 0,
                HighResolution14Bit: message.HighResolution14Bit,
                Parameter: message.Parameter,
                Data: message.Data));
            return ValueTask.CompletedTask;
        }

        private static ControlScriptMIDIMessageKind ToScriptMessageKind(ControlMIDIMessageType messageType) =>
            messageType switch
            {
                ControlMIDIMessageType.ControlChange => ControlScriptMIDIMessageKind.ControlChange,
                ControlMIDIMessageType.NoteOn => ControlScriptMIDIMessageKind.NoteOn,
                ControlMIDIMessageType.NoteOff => ControlScriptMIDIMessageKind.NoteOff,
                ControlMIDIMessageType.PolyphonicAftertouch => ControlScriptMIDIMessageKind.PolyphonicAftertouch,
                ControlMIDIMessageType.ProgramChange => ControlScriptMIDIMessageKind.ProgramChange,
                ControlMIDIMessageType.ChannelAftertouch => ControlScriptMIDIMessageKind.ChannelAftertouch,
                ControlMIDIMessageType.PitchBend => ControlScriptMIDIMessageKind.PitchBend,
                ControlMIDIMessageType.SysEx => ControlScriptMIDIMessageKind.SysEx,
                ControlMIDIMessageType.MIDITimeCode => ControlScriptMIDIMessageKind.MIDITimeCode,
                ControlMIDIMessageType.SongPosition => ControlScriptMIDIMessageKind.SongPosition,
                ControlMIDIMessageType.SongSelect => ControlScriptMIDIMessageKind.SongSelect,
                ControlMIDIMessageType.TuneRequest => ControlScriptMIDIMessageKind.TuneRequest,
                ControlMIDIMessageType.TimingClock => ControlScriptMIDIMessageKind.TimingClock,
                ControlMIDIMessageType.Start => ControlScriptMIDIMessageKind.Start,
                ControlMIDIMessageType.Continue => ControlScriptMIDIMessageKind.Continue,
                ControlMIDIMessageType.Stop => ControlScriptMIDIMessageKind.Stop,
                ControlMIDIMessageType.ActiveSensing => ControlScriptMIDIMessageKind.ActiveSensing,
                ControlMIDIMessageType.Reset => ControlScriptMIDIMessageKind.Reset,
                ControlMIDIMessageType.NRPN => ControlScriptMIDIMessageKind.NRPN,
                ControlMIDIMessageType.RPN => ControlScriptMIDIMessageKind.RPN,
                _ => throw new InvalidOperationException($"Unexpected MIDI message type '{messageType}'."),
            };
    }

    private sealed record SentMIDIMessage(
        ControlScriptMIDIMessageKind Kind,
        Guid? EndpointId,
        int Channel,
        int? Controller,
        int? Note,
        int Value,
        bool HighResolution14Bit,
        int? Parameter = null,
        byte[]? Data = null);
}
