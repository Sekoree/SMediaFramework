namespace S.Control;

/// <summary>
/// Generates built-in profile documents. Shipped JSON under <c>Profiles/</c> is the runtime source of truth;
/// regenerate those files from here when the catalog changes.
/// </summary>
public static class BuiltInControlDeviceProfileFactory
{
    public static IReadOnlyList<ControlDeviceProfile> All() =>
    [
        CreateXTouchMiniProfile(),
        CreateBcf2000Profile(),
        CreateX32Profile(),
        CreateXAirProfile(),
    ];

    public static ControlDeviceProfile CreateXTouchMiniProfile()
    {
        var controls = new List<ControlControlProfile>
        {
            new()
            {
                Id = "xtouch.layer.a",
                DisplayName = "Layer A",
                Kind = ControlProfileControlKind.LayerButton,
                MidiNote = 84,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            },
            new()
            {
                Id = "xtouch.layer.b",
                DisplayName = "Layer B",
                Kind = ControlProfileControlKind.LayerButton,
                MidiNote = 85,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            },
        };

        for (var i = 0; i < 8; i++)
        {
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.encoder.{i + 1}",
                DisplayName = $"Encoder {i + 1}",
                Kind = ControlProfileControlKind.Encoder,
                MidiController = 16 + i,
                ValueMode = ControlProfileValueMode.RelativeEncoder,
                IncrementValues = Enumerable.Range(1, 10).ToList(),
                DecrementValues = Enumerable.Range(65, 8).ToList(),
            });
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.encoder.{i + 1}.push",
                DisplayName = $"Encoder {i + 1} Push",
                Kind = ControlProfileControlKind.EncoderPush,
                MidiNote = 32 + i,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }

        var buttonNotes = new[] { 89, 90, 40, 41, 42, 43, 44, 45, 87, 88, 91, 92, 86, 93, 94, 95 };
        for (var i = 0; i < buttonNotes.Length; i++)
        {
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.button.{i + 1}",
                DisplayName = $"Button {i + 1}",
                Kind = ControlProfileControlKind.Button,
                MidiNote = buttonNotes[i],
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }

        controls.Add(new ControlControlProfile
        {
            Id = "xtouch.master-fader",
            DisplayName = "Master Fader",
            Kind = ControlProfileControlKind.Fader,
            ValueMode = ControlProfileValueMode.PitchWheel,
        });

        return new ControlDeviceProfile
        {
            Id = "behringer.xtouch-mini.mc",
            DisplayName = "Behringer X-Touch Mini (MC Mode)",
            Protocol = ControlDeviceProtocol.Midi,
            Version = "1.0",
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "midi-in",
                    DisplayName = "MIDI Input",
                    Kind = ControlDevicePortKind.MidiInput,
                },
                new ControlDevicePortProfile
                {
                    Id = "midi-out",
                    DisplayName = "MIDI Output",
                    Kind = ControlDevicePortKind.MidiOutput,
                },
            ],
            Controls = controls,
            Layers =
            [
                new ControlLayerProfile { Id = "layer.a", DisplayName = "Layer A" },
                new ControlLayerProfile { Id = "layer.b", DisplayName = "Layer B" },
            ],
        };
    }

    /// <summary>
    /// Behringer BCF2000 (8 motor faders + 8 rotary encoders + button banks). Encoders and faders are
    /// configured for 14-bit (high-resolution) absolute CC — the input combiner pairs each coarse CC with
    /// its fine partner (controller + 32), and the value range is the full 14-bit span (0–16383). All eight
    /// faders/encoders and the LED buttons are read/write, so feedback can motorise the faders, position the
    /// encoder LED rings, and drive the button LEDs. Mirrors <c>Reference/BCF2000.txt</c>.
    /// </summary>
    public static ControlDeviceProfile CreateBcf2000Profile()
    {
        const int HighResMax = 0x3FFF; // 16383 — the actual highest 14-bit value

        var controls = new List<ControlControlProfile>();

        for (var i = 0; i < 8; i++)
        {
            // Motor faders: 14-bit CC 0–7 (fine partners 32–39).
            controls.Add(new ControlControlProfile
            {
                Id = $"bcf.fader.{i + 1}",
                DisplayName = $"Motor Fader {i + 1}",
                Kind = ControlProfileControlKind.Fader,
                MidiController = i,
                ValueMode = ControlProfileValueMode.Absolute14Bit,
                MidiHighResolution14Bit = true,
                MidiValueMin = 0,
                MidiValueMax = HighResMax,
            });
            // Rotary encoders: 14-bit CC 10–17 (fine partners 42–49).
            controls.Add(new ControlControlProfile
            {
                Id = $"bcf.encoder.{i + 1}",
                DisplayName = $"Encoder {i + 1}",
                Kind = ControlProfileControlKind.Encoder,
                MidiController = 10 + i,
                ValueMode = ControlProfileValueMode.Absolute14Bit,
                MidiHighResolution14Bit = true,
                MidiValueMin = 0,
                MidiValueMax = HighResMax,
            });
            // Encoder press: notes 0–7 (momentary).
            controls.Add(new ControlControlProfile
            {
                Id = $"bcf.encoder.{i + 1}.push",
                DisplayName = $"Encoder {i + 1} Push",
                Kind = ControlProfileControlKind.EncoderPush,
                MidiNote = i,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }

        // Latching, LED-backed button banks (NoteOn velocity 127 = lit). Row 1 + Row 2 are 8-wide;
        // Groups 3/4/6 are 4-wide. Group 5 (notes 50–51) is momentary with no LED (handled the same here).
        AddBcfButtons(controls, "row1", "Row 1 Button", baseNote: 10, count: 8);
        AddBcfButtons(controls, "row2", "Row 2 Button", baseNote: 20, count: 8);
        AddBcfButtons(controls, "group3", "Group 3 Button", baseNote: 30, count: 4);
        AddBcfButtons(controls, "group4", "Group 4 Button", baseNote: 40, count: 4);
        AddBcfButtons(controls, "group5", "Group 5 Button", baseNote: 50, count: 2);
        AddBcfButtons(controls, "group6", "Group 6 Button", baseNote: 60, count: 4);

        return new ControlDeviceProfile
        {
            Id = "behringer.bcf2000",
            DisplayName = "Behringer BCF2000",
            Protocol = ControlDeviceProtocol.Midi,
            Version = "1.0",
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "midi-in",
                    DisplayName = "MIDI Input",
                    Kind = ControlDevicePortKind.MidiInput,
                },
                new ControlDevicePortProfile
                {
                    Id = "midi-out",
                    DisplayName = "MIDI Output",
                    Kind = ControlDevicePortKind.MidiOutput,
                },
            ],
            Controls = controls,
            Layers =
            [
                new ControlLayerProfile { Id = "layer.1", DisplayName = "Channels 1-8" },
                new ControlLayerProfile { Id = "layer.2", DisplayName = "Channels 9-16" },
                new ControlLayerProfile { Id = "layer.3", DisplayName = "Channels 17-24" },
                new ControlLayerProfile { Id = "layer.4", DisplayName = "Channels 25-32" },
                new ControlLayerProfile { Id = "layer.5", DisplayName = "Outputs" },
            ],
        };
    }

    private static void AddBcfButtons(List<ControlControlProfile> controls, string idPart, string displayPrefix, int baseNote, int count)
    {
        for (var i = 0; i < count; i++)
        {
            controls.Add(new ControlControlProfile
            {
                Id = $"bcf.button.{idPart}.{i + 1}",
                DisplayName = $"{displayPrefix} {i + 1}",
                Kind = ControlProfileControlKind.Button,
                MidiNote = baseNote + i,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }
    }

    public static ControlDeviceProfile CreateX32Profile()
    {
        var commands = new List<ControlCommandProfile>();

        for (var channel = 1; channel <= 32; channel++)
        {
            commands.Add(NormalizedCommand($"x32.ch.{channel:00}.fader", $"Ch {channel:00} Fader", X32Presets.ChannelFaderAddress(channel)));
            commands.Add(BooleanCommand($"x32.ch.{channel:00}.mute", $"Ch {channel:00} Mute", X32Presets.ChannelMuteAddress(channel)));
            commands.Add(NormalizedCommand($"x32.ch.{channel:00}.pan", $"Ch {channel:00} Pan", X32Presets.ChannelPanAddress(channel)));
            commands.Add(BooleanCommand($"x32.ch.{channel:00}.solo", $"Ch {channel:00} Solo", X32Presets.ChannelSoloStatusAddress(channel), ControlCommandAccess.ReadOnly));
        }

        for (var dca = 1; dca <= 8; dca++)
        {
            commands.Add(NormalizedCommand($"x32.dca.{dca}.fader", $"DCA {dca} Fader", X32Presets.DcaFaderAddress(dca)));
            commands.Add(BooleanCommand($"x32.dca.{dca}.mute", $"DCA {dca} Mute", X32Presets.DcaMuteAddress(dca)));
        }

        for (var bus = 1; bus <= 16; bus++)
        {
            commands.Add(NormalizedCommand($"x32.bus.{bus:00}.fader", $"Bus {bus:00} Fader", X32Presets.BusFaderAddress(bus)));
            commands.Add(BooleanCommand($"x32.bus.{bus:00}.mute", $"Bus {bus:00} Mute", X32Presets.BusMuteAddress(bus)));
        }

        for (var matrix = 1; matrix <= 6; matrix++)
        {
            commands.Add(NormalizedCommand($"x32.matrix.{matrix:00}.fader", $"Matrix {matrix:00} Fader", X32Presets.MatrixFaderAddress(matrix)));
            commands.Add(BooleanCommand($"x32.matrix.{matrix:00}.mute", $"Matrix {matrix:00} Mute", X32Presets.MatrixMuteAddress(matrix)));
        }

        commands.Add(NormalizedCommand("x32.main.st.fader", "Main Stereo Fader", X32Presets.MainStereoFaderAddress()));
        commands.Add(BooleanCommand("x32.main.st.mute", "Main Stereo Mute", X32Presets.MainStereoMuteAddress()));

        return new ControlDeviceProfile
        {
            Id = "behringer.x32.osc",
            DisplayName = "Behringer X32 / Midas M32 OSC",
            Protocol = ControlDeviceProtocol.Osc,
            Version = "1.0",
            DefaultOscPort = X32Presets.DefaultPort,
            Behaviors = new ControlDeviceProfileBehaviors
            {
                ProtocolMaintenance = new ControlProtocolMaintenanceBehavior { RenewIntervalMs = 8000 },
                MeterBlobDecoder = "x32",
            },
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "osc-remote",
                    DisplayName = "OSC Remote",
                    Kind = ControlDevicePortKind.OscRemote,
                },
                new ControlDevicePortProfile
                {
                    Id = "osc-listener",
                    DisplayName = "OSC Listener",
                    Kind = ControlDevicePortKind.OscListener,
                },
            ],
            Commands = commands,
            Tasks =
            [
                new ControlDeviceTaskProfile
                {
                    Id = "x32.xremote",
                    DisplayName = "Maintain /xremote",
                    IsDefaultEnabled = true,
                    Kind = ControlDeviceTaskKind.ProtocolMaintenance,
                    Address = "/xremote",
                    IntervalMs = 8000,
                },
                new ControlDeviceTaskProfile
                {
                    Id = "x32.subscribe.ch01.fader",
                    DisplayName = "Subscribe Ch 01 fader (optional)",
                    Kind = ControlDeviceTaskKind.ProtocolMaintenance,
                    Address = "/subscribe",
                    IntervalMs = 8000,
                    Arguments =
                    [
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = X32Presets.ChannelFaderAddress(1) },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 50 },
                    ],
                },
                new ControlDeviceTaskProfile
                {
                    Id = "x32.meters.bank6",
                    DisplayName = "Subscribe meter bank 6 (optional)",
                    Kind = ControlDeviceTaskKind.ProtocolMaintenance,
                    Address = "/meters",
                    IntervalMs = 8000,
                    Arguments =
                    [
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = "/meters/6" },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 16 },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 1 },
                    ],
                },
            ],
        };
    }

    public static ControlDeviceProfile CreateXAirProfile()
    {
        var commands = new List<ControlCommandProfile>();

        for (var channel = 1; channel <= XAirPresets.ChannelCount; channel++)
        {
            commands.Add(NormalizedCommand($"xair.ch.{channel:00}.fader", $"Ch {channel:00} Fader", XAirPresets.ChannelFaderAddress(channel)));
            commands.Add(BooleanCommand($"xair.ch.{channel:00}.mute", $"Ch {channel:00} Mute", XAirPresets.ChannelMuteAddress(channel)));
            commands.Add(NormalizedCommand($"xair.ch.{channel:00}.pan", $"Ch {channel:00} Pan", XAirPresets.ChannelPanAddress(channel)));
            commands.Add(BooleanCommand($"xair.ch.{channel:00}.solo", $"Ch {channel:00} Solo", XAirPresets.ChannelSoloStatusAddress(channel), ControlCommandAccess.ReadOnly));
        }

        for (var bus = 1; bus <= XAirPresets.BusCount; bus++)
        {
            commands.Add(NormalizedCommand($"xair.bus.{bus}.fader", $"Bus {bus} Fader", XAirPresets.BusFaderAddress(bus)));
            commands.Add(BooleanCommand($"xair.bus.{bus}.mute", $"Bus {bus} Mute", XAirPresets.BusMuteAddress(bus)));
        }

        for (var dca = 1; dca <= XAirPresets.DcaCount; dca++)
        {
            commands.Add(NormalizedCommand($"xair.dca.{dca}.fader", $"DCA {dca} Fader", XAirPresets.DcaFaderAddress(dca)));
            commands.Add(BooleanCommand($"xair.dca.{dca}.mute", $"DCA {dca} Mute", XAirPresets.DcaMuteAddress(dca)));
        }

        commands.Add(NormalizedCommand("xair.lr.fader", "Main LR Fader", XAirPresets.MainLrFaderAddress()));
        commands.Add(BooleanCommand("xair.lr.mute", "Main LR Mute", XAirPresets.MainLrMuteAddress()));

        return new ControlDeviceProfile
        {
            Id = "behringer.xair.osc",
            DisplayName = "Behringer X-Air / Midas M-Air OSC",
            Protocol = ControlDeviceProtocol.Osc,
            Version = "1.0",
            DefaultOscPort = XAirPresets.DefaultPort,
            Behaviors = new ControlDeviceProfileBehaviors
            {
                ProtocolMaintenance = new ControlProtocolMaintenanceBehavior { RenewIntervalMs = 8000 },
                MeterBlobDecoder = "x32",
            },
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "osc-remote",
                    DisplayName = "OSC Remote",
                    Kind = ControlDevicePortKind.OscRemote,
                },
                new ControlDevicePortProfile
                {
                    Id = "osc-listener",
                    DisplayName = "OSC Listener",
                    Kind = ControlDevicePortKind.OscListener,
                },
            ],
            Commands = commands,
            Tasks =
            [
                new ControlDeviceTaskProfile
                {
                    Id = "xair.xremote",
                    DisplayName = "Maintain /xremote",
                    IsDefaultEnabled = true,
                    Kind = ControlDeviceTaskKind.ProtocolMaintenance,
                    Address = "/xremote",
                    IntervalMs = 8000,
                },
            ],
        };
    }

    private static ControlCommandProfile NormalizedCommand(string id, string displayName, string address) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Address = address,
            ValueKind = ControlCommandValueKind.NormalizedFloat,
            MinValue = 0,
            MaxValue = 1,
            CacheKey = address,
        };

    private static ControlCommandProfile BooleanCommand(
        string id,
        string displayName,
        string address,
        ControlCommandAccess access = ControlCommandAccess.ReadWrite) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Address = address,
            ValueKind = ControlCommandValueKind.BooleanInt,
            Access = access,
            MinValue = 0,
            MaxValue = 1,
            CacheKey = address,
        };
}
