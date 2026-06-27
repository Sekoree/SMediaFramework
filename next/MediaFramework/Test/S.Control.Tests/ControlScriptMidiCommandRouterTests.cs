using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptMidiCommandRouterTests
{
    [Fact]
    public async Task SendAllAsync_RoutesMessagesToMultipleMidiDevicesByAlias()
    {
        var xtouchId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var sender = new RecordingMidiSender();
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    MidiDevice(xtouchId, "X-Touch Mini", "xtouch", "X-Touch MINI"),
                    MidiDevice(backupId, "Backup Surface", "backup", "Backup MIDI Out"),
                ],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptMidiMessage("xtouch", ControlScriptMidiMessageKind.ControlChange, 1, Controller: 16, Value: 64),
            new ControlScriptMidiMessage("xtouch", ControlScriptMidiMessageKind.NoteOn, 1, Note: 89, Velocity: 127),
            new ControlScriptMidiMessage("backup", ControlScriptMidiMessageKind.ProgramChange, 2, Value: 5),
            new ControlScriptMidiMessage("backup", ControlScriptMidiMessageKind.PitchBend, 2, Value: 8192),
        ]);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Collection(
            sender.Sent,
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.ControlChange, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(1, sent.Channel);
                Assert.Equal(16, sent.Controller);
                Assert.Equal(64, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.NoteOn, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(89, sent.Note);
                Assert.Equal(127, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.ProgramChange, sent.Kind);
                Assert.Equal(backupId, sent.EndpointId);
                Assert.Equal(2, sent.Channel);
                Assert.Equal(5, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.PitchBend, sent.Kind);
                Assert.Equal(backupId, sent.EndpointId);
                Assert.Equal(8192, sent.Value);
            });
    }

    [Fact]
    public async Task SendAsync_DoesNotRouteToDisabledMidiDevice()
    {
        var sender = new RecordingMidiSender();
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices = [MidiDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", "X-Touch MINI", isEnabled: false)],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMidiMessage("xtouch", ControlScriptMidiMessageKind.ControlChange, 1, Controller: 16, Value: 64));

        Assert.False(result.Succeeded);
        Assert.Contains("No enabled MIDI device matches key 'xtouch'", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsAmbiguousProfileKey()
    {
        var sender = new RecordingMidiSender();
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    MidiDevice(Guid.NewGuid(), "X-Touch A", "xtouch-a", "X-Touch A", profileId: "behringer.xtouch-mini.mc"),
                    MidiDevice(Guid.NewGuid(), "X-Touch B", "xtouch-b", "X-Touch B", profileId: "behringer.xtouch-mini.mc"),
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMidiMessage("behringer.xtouch-mini.mc", ControlScriptMidiMessageKind.ControlChange, 1, Controller: 16, Value: 64));

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsMidiDeviceWithoutOutputBinding()
    {
        var sender = new RecordingMidiSender();
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "X-Touch Mini",
                        Protocol = ControlDeviceProtocol.Midi,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig { Alias = "xtouch" },
                    },
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptMidiMessage("xtouch", ControlScriptMidiMessageKind.NoteOff, 1, Note: 89));

        Assert.False(result.Succeeded);
        Assert.Contains("does not have an output binding", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_ReturnsFailureWhenSenderIsMissing()
    {
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices = [MidiDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", "X-Touch MINI")],
            },
            sender: null);

        var result = await router.SendAsync(
            new ControlScriptMidiMessage("xtouch", ControlScriptMidiMessageKind.NoteOn, 1, Note: 89, Velocity: 127));

        Assert.False(result.Succeeded);
        Assert.Contains("No MIDI sender is configured", result.ErrorMessage);
    }

    [Fact]
    public async Task SendAllAsync_RoutesExtendedMidiMessagesByAlias()
    {
        var deviceId = Guid.NewGuid();
        var sender = new RecordingMidiSender();
        var router = new ControlScriptMidiCommandRouter(
            new ControlSystemConfig
            {
                Devices = [MidiDevice(deviceId, "Synth", "synth", "Synth Out")],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.PolyphonicAftertouch, 1, Note: 60, Value: 70),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.ChannelAftertouch, 1, Value: 71),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.SysEx, Data: [0xF0, 0x7D, 0x01, 0xF7]),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.MIDITimeCode, Value: 0x12),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.SongPosition, Value: 96),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.SongSelect, Value: 3),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.TuneRequest),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.TimingClock),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.Start),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.Continue),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.Stop),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.ActiveSensing),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.Reset),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.NRPN, 1, Parameter: 100, Value: 200),
            new ControlScriptMidiMessage("synth", ControlScriptMidiMessageKind.RPN, 1, Parameter: 101, Value: 201),
        ]);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(15, sender.Sent.Count);
        Assert.All(sender.Sent, sent => Assert.Equal(deviceId, sent.EndpointId));
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.PolyphonicAftertouch && sent.Note == 60 && sent.Value == 70);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.ChannelAftertouch && sent.Value == 71);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.SysEx && sent.Data?.SequenceEqual(new byte[] { 0xF0, 0x7D, 0x01, 0xF7 }) == true);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.MIDITimeCode && sent.Value == 0x12);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.SongPosition && sent.Value == 96);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.SongSelect && sent.Value == 3);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.TuneRequest);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.TimingClock);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.Start);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.Continue);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.Stop);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.ActiveSensing);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.Reset);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.NRPN && sent.Parameter == 100 && sent.Value == 200);
        Assert.Contains(sender.Sent, sent => sent.Kind == ControlScriptMidiMessageKind.RPN && sent.Parameter == 101 && sent.Value == 201);
    }

    private static ControlDeviceInstanceConfig MidiDevice(
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
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = isEnabled,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MidiOutputDeviceName = outputName,
            },
        };

    private sealed class RecordingMidiSender : IControlMidiSender
    {
        public List<SentMidiMessage> Sent { get; } = new();

        public ValueTask SendControlChangeAsync(
            Guid? endpointId,
            int channel,
            int controller,
            int value,
            bool highResolution14Bit,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ControlChange,
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
            Sent.Add(new SentMidiMessage(
                isNoteOn ? ControlScriptMidiMessageKind.NoteOn : ControlScriptMidiMessageKind.NoteOff,
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
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ProgramChange,
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
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.PitchBend,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: value,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendMidiMessageAsync(
            Guid? endpointId,
            ControlMidiMessagePayload message,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
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

        private static ControlScriptMidiMessageKind ToScriptMessageKind(ControlMidiMessageType messageType) =>
            messageType switch
            {
                ControlMidiMessageType.ControlChange => ControlScriptMidiMessageKind.ControlChange,
                ControlMidiMessageType.NoteOn => ControlScriptMidiMessageKind.NoteOn,
                ControlMidiMessageType.NoteOff => ControlScriptMidiMessageKind.NoteOff,
                ControlMidiMessageType.PolyphonicAftertouch => ControlScriptMidiMessageKind.PolyphonicAftertouch,
                ControlMidiMessageType.ProgramChange => ControlScriptMidiMessageKind.ProgramChange,
                ControlMidiMessageType.ChannelAftertouch => ControlScriptMidiMessageKind.ChannelAftertouch,
                ControlMidiMessageType.PitchBend => ControlScriptMidiMessageKind.PitchBend,
                ControlMidiMessageType.SysEx => ControlScriptMidiMessageKind.SysEx,
                ControlMidiMessageType.MIDITimeCode => ControlScriptMidiMessageKind.MIDITimeCode,
                ControlMidiMessageType.SongPosition => ControlScriptMidiMessageKind.SongPosition,
                ControlMidiMessageType.SongSelect => ControlScriptMidiMessageKind.SongSelect,
                ControlMidiMessageType.TuneRequest => ControlScriptMidiMessageKind.TuneRequest,
                ControlMidiMessageType.TimingClock => ControlScriptMidiMessageKind.TimingClock,
                ControlMidiMessageType.Start => ControlScriptMidiMessageKind.Start,
                ControlMidiMessageType.Continue => ControlScriptMidiMessageKind.Continue,
                ControlMidiMessageType.Stop => ControlScriptMidiMessageKind.Stop,
                ControlMidiMessageType.ActiveSensing => ControlScriptMidiMessageKind.ActiveSensing,
                ControlMidiMessageType.Reset => ControlScriptMidiMessageKind.Reset,
                ControlMidiMessageType.NRPN => ControlScriptMidiMessageKind.NRPN,
                ControlMidiMessageType.RPN => ControlScriptMidiMessageKind.RPN,
                _ => throw new InvalidOperationException($"Unexpected MIDI message type '{messageType}'."),
            };
    }

    private sealed record SentMidiMessage(
        ControlScriptMidiMessageKind Kind,
        Guid? EndpointId,
        int Channel,
        int? Controller,
        int? Note,
        int Value,
        bool HighResolution14Bit,
        int? Parameter = null,
        byte[]? Data = null);
}
