using HaPlay.ControlGraph;
using HaPlay.Models;
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
    }

    private sealed record SentMidiMessage(
        ControlScriptMidiMessageKind Kind,
        Guid? EndpointId,
        int Channel,
        int? Controller,
        int? Note,
        int Value,
        bool HighResolution14Bit);
}
