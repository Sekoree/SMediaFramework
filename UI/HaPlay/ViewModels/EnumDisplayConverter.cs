using System.Globalization;
using Avalonia.Data.Converters;
using S.Control;

namespace HaPlay.ViewModels;

public sealed class EnumDisplayConverter : IValueConverter
{
    public static EnumDisplayConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CueTriggerMode.Manual => "Manual",
            CueTriggerMode.AutoFollow => "Auto-follow",
            CueTriggerMode.AutoContinue => "Auto-continue",

            CueGroupFireMode.FirstCueOnly => "First cue only",
            CueGroupFireMode.FireAllSimultaneously => "Fire all together",
            CueGroupFireMode.ArmedList => "Armed list",

            CueEndBehavior.Stop => "Stop",
            CueEndBehavior.FreezeLastFrame => "Freeze last frame",
            CueEndBehavior.Loop => "Loop",
            CueEndBehavior.FadeOutAndStop => "Fade out and stop",

            CueLayerPosition.Cover => "Cover",
            CueLayerPosition.Letterbox => "Letterbox",
            CueLayerPosition.Center => "Center",
            CueLayerPosition.FillWidth => "Fill width",
            CueLayerPosition.FillHeight => "Fill height",
            CueLayerPosition.Stretch => "Stretch",

            TextAlignH.Left => "Left",
            TextAlignH.Center => "Center",
            TextAlignH.Right => "Right",
            TextAlignV.Top => "Top",
            TextAlignV.Middle => "Middle",
            TextAlignV.Bottom => "Bottom",

            CueActionKind.OSCOut => "OSC output",
            CueActionKind.MIDIOut => "MIDI output",

            CueMIDICommandType.NRPN => "NRPN",
            CueMIDICommandType.RPN => "RPN",
            CueMIDICommandType.NoteOff => "Note off",
            CueMIDICommandType.NoteOn => "Note on",
            CueMIDICommandType.PolyphonicAftertouch => "Polyphonic aftertouch",
            CueMIDICommandType.ControlChange => "Control change",
            CueMIDICommandType.HighResolutionControlChange => "14-bit control change",
            CueMIDICommandType.ProgramChange => "Program change",
            CueMIDICommandType.ChannelAftertouch => "Channel aftertouch",
            CueMIDICommandType.PitchBend => "Pitch bend",
            CueMIDICommandType.SysEx => "SysEx",
            CueMIDICommandType.MIDITimeCode => "MIDI time code",
            CueMIDICommandType.SongPosition => "Song position",
            CueMIDICommandType.SongSelect => "Song select",
            CueMIDICommandType.TuneRequest => "Tune request",
            CueMIDICommandType.TimingClock => "Timing clock",
            CueMIDICommandType.Start => "Start",
            CueMIDICommandType.Continue => "Continue",
            CueMIDICommandType.Stop => "Stop",
            CueMIDICommandType.ActiveSensing => "Active sensing",
            CueMIDICommandType.Reset => "Reset",

            AppBaseTheme.Classic => "Classic",
            AppBaseTheme.Simple => "Simple",
            AppBaseTheme.Fluent => "Fluent",
            AppThemeMode.System => "Follow system",
            AppThemeMode.Light => "Light",
            AppThemeMode.Dark => "Dark",
            AppDensityMode.Compact => "Compact",
            AppDensityMode.Normal => "Comfortable",
            PlayerOutputPreset.AsSource => "Match source",
            PlayerOutputPreset.Preset1080p60 => "1080p60",
            PlayerOutputPreset.Preset720p60 => "720p60",
            PlayerOutputPreset.Custom => "Custom",
            PlayerTransitionMode.Cut => "Cut",
            PlayerTransitionMode.Fade => "Fade from black",
            PlayerTransitionMode.IdleImage => "Idle image",
            AudioRouteMixMode.Stereo => "Stereo",
            AudioRouteMixMode.Swap => "Swap L/R",
            AudioRouteMixMode.MonoLeft => "Mono from left",
            AudioRouteMixMode.MonoRight => "Mono from right",
            AudioRouteMixMode.Silence => "Silence",

            VideoSurfaceMode.Windowed => "Windowed",
            VideoSurfaceMode.FullScreen => "Fullscreen",
            NDIOutputStreamMode.VideoAndAudio => "Video and audio",
            NDIOutputStreamMode.VideoOnly => "Video only",
            NDIOutputStreamMode.AudioOnly => "Audio only",

            ControlScriptScope.Project => "Project",
            ControlScriptScope.Device => "Device",
            ControlScriptScope.Endpoint => "Endpoint",
            ControlScriptScope.Layer => "Layer",
            ControlScriptFailureMode.KeepRunning => "Keep running",
            ControlScriptFailureMode.DisableScript => "Disable script",
            ControlScriptFailureMode.DisableScope => "Disable scope",
            ControlScriptFailureMode.FaultControlSystem => "Fault control system",
            ControlScriptTriggerKind.DeviceEnabled => "Device enabled",
            ControlScriptTriggerKind.DeviceDisabled => "Device disabled",
            ControlScriptTriggerKind.DeviceHealthChanged => "Device health changed",
            ControlScriptTriggerKind.MIDIMessage => "MIDI message",
            ControlScriptTriggerKind.MIDIControlChange => "MIDI control change",
            ControlScriptTriggerKind.MIDINote => "MIDI note",
            ControlScriptTriggerKind.OSCMessage => "OSC message",
            ControlScriptTriggerKind.OSCCacheChanged => "OSC cache changed",
            ControlScriptTriggerKind.LayerEnabled => "Layer enabled",
            ControlScriptTriggerKind.LayerDisabled => "Layer disabled",
            ControlScriptTriggerKind.Periodic => "Periodic",
            ControlScriptTriggerKind.Manual => "Manual",
            ControlMIDIMessageType.Unknown => "Any MIDI message",
            ControlMIDIMessageType.NRPN => "NRPN",
            ControlMIDIMessageType.RPN => "RPN",
            ControlMIDIMessageType.NoteOff => "Note off",
            ControlMIDIMessageType.NoteOn => "Note on",
            ControlMIDIMessageType.PolyphonicAftertouch => "Polyphonic aftertouch",
            ControlMIDIMessageType.ControlChange => "Control change",
            ControlMIDIMessageType.ProgramChange => "Program change",
            ControlMIDIMessageType.ChannelAftertouch => "Channel aftertouch",
            ControlMIDIMessageType.PitchBend => "Pitch bend",
            ControlMIDIMessageType.SysEx => "SysEx",
            ControlMIDIMessageType.MIDITimeCode => "MIDI time code",
            ControlMIDIMessageType.SongPosition => "Song position",
            ControlMIDIMessageType.SongSelect => "Song select",
            ControlMIDIMessageType.TuneRequest => "Tune request",
            ControlMIDIMessageType.TimingClock => "Timing clock",
            ControlMIDIMessageType.Start => "Start",
            ControlMIDIMessageType.Continue => "Continue",
            ControlMIDIMessageType.Stop => "Stop",
            ControlMIDIMessageType.ActiveSensing => "Active sensing",
            ControlMIDIMessageType.Reset => "Reset",

            null => string.Empty,
            _ => SplitPascalCase(value.ToString() ?? string.Empty),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = new List<char>(value.Length + 8) { value[0] };
        for (var i = 1; i < value.Length; i++)
        {
            var current = value[i];
            var previous = value[i - 1];
            if (char.IsUpper(current) && (char.IsLower(previous) || char.IsDigit(previous)))
                chars.Add(' ');
            chars.Add(current);
        }
        return new string(chars.ToArray());
    }
}
