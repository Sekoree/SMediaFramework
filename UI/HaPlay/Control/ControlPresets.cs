using HaPlay.Models;

namespace HaPlay.ControlGraph;

public static class X32Presets
{
    public const int DefaultPort = 10023;

    public static string ChannelFaderAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/fader";

    public static string ChannelMuteAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/on";

    public static string ChannelPanAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/pan";

    public static string DcaFaderAddress(int dca) =>
        $"/dca/{Math.Clamp(dca, 1, 8)}/fader";

    public static string MainStereoFaderAddress() => "/main/st/mix/fader";

    public static X32CustomLayerConfig CreateDefaultBcf2000ChannelLayer(
        string name = "BCF2000 X32 Channels 1-8",
        int firstChannel = 1,
        int midiChannel = 1,
        int firstController = 0)
    {
        var slots = new List<X32CustomLayerSlotConfig>();
        for (var i = 0; i < 8; i++)
        {
            var target = Math.Clamp(firstChannel + i, 1, 32);
            slots.Add(new X32CustomLayerSlotConfig
            {
                SlotIndex = i,
                Label = $"Ch {target:00}",
                TargetKind = X32LayerTargetKind.Channel,
                TargetIndex = target,
                MidiChannel = midiChannel,
                MidiController = firstController + i,
                HighResolution14Bit = true,
            });
        }

        return new X32CustomLayerConfig { Name = name, Slots = slots };
    }

    public static X32CustomLayerConfig CreateDefaultXTouchMiniChannelLayer(
        string name = "X-Touch Mini X32 Channels 1-8",
        int firstChannel = 1,
        int midiChannel = 1,
        int firstController = 16)
    {
        var slots = new List<X32CustomLayerSlotConfig>();
        for (var i = 0; i < 8; i++)
        {
            var target = Math.Clamp(firstChannel + i, 1, 32);
            slots.Add(new X32CustomLayerSlotConfig
            {
                SlotIndex = i,
                Label = $"Ch {target:00}",
                TargetKind = X32LayerTargetKind.Channel,
                TargetIndex = target,
                MidiChannel = midiChannel,
                MidiController = firstController + i,
                HighResolution14Bit = false,
            });
        }

        return new X32CustomLayerConfig { Name = name, Slots = slots };
    }

    public static ControlGraphConfig CreateMidiFaderToX32Graph(
        X32CustomLayerConfig layer,
        string host,
        int port = DefaultPort)
    {
        var graph = new ControlGraphConfig
        {
            Name = $"{layer.Name} Graph",
            IsEnabled = false,
        };

        foreach (var slot in layer.Slots.OrderBy(s => s.SlotIndex))
        {
            var midi = new ControlNodeConfig
            {
                DisplayName = $"{slot.Label} MIDI",
                Kind = ControlNodeKind.MidiInput,
                X = 0,
                Y = slot.SlotIndex * 120,
                Settings = new MidiInputControlNodeSettings
                {
                    Channel = slot.MidiChannel,
                    Controller = slot.MidiController,
                    HighResolution14Bit = slot.HighResolution14Bit,
                },
            };
            var map = new ControlNodeConfig
            {
                DisplayName = $"{slot.Label} Normalize",
                Kind = ControlNodeKind.MapRange,
                X = 240,
                Y = slot.SlotIndex * 120,
                Settings = new MapRangeControlNodeSettings
                {
                    InputMin = 0,
                    InputMax = slot.HighResolution14Bit ? 16383 : 127,
                    OutputMin = 0,
                    OutputMax = 1,
                },
            };
            var x32 = new ControlNodeConfig
            {
                DisplayName = $"{slot.Label} X32 Fader",
                Kind = ControlNodeKind.X32ChannelFader,
                X = 480,
                Y = slot.SlotIndex * 120,
                Settings = new X32ChannelFaderControlNodeSettings
                {
                    Host = host,
                    Port = port,
                    Channel = slot.TargetIndex,
                },
            };

            graph.Nodes.Add(midi);
            graph.Nodes.Add(map);
            graph.Nodes.Add(x32);
            graph.Connections.Add(new ControlConnectionConfig
            {
                FromNodeId = midi.Id,
                ToNodeId = map.Id,
            });
            graph.Connections.Add(new ControlConnectionConfig
            {
                FromNodeId = map.Id,
                ToNodeId = x32.Id,
            });
        }

        return graph;
    }
}
