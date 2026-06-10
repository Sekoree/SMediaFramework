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

            CueActionKind.OscOut => "OSC output",
            CueActionKind.MidiOut => "MIDI output",

            CueMidiCommandType.NRPN => "NRPN",
            CueMidiCommandType.RPN => "RPN",
            CueMidiCommandType.NoteOff => "Note off",
            CueMidiCommandType.NoteOn => "Note on",
            CueMidiCommandType.PolyphonicAftertouch => "Polyphonic aftertouch",
            CueMidiCommandType.ControlChange => "Control change",
            CueMidiCommandType.HighResolutionControlChange => "14-bit control change",
            CueMidiCommandType.ProgramChange => "Program change",
            CueMidiCommandType.ChannelAftertouch => "Channel aftertouch",
            CueMidiCommandType.PitchBend => "Pitch bend",
            CueMidiCommandType.SysEx => "SysEx",
            CueMidiCommandType.MIDITimeCode => "MIDI time code",
            CueMidiCommandType.SongPosition => "Song position",
            CueMidiCommandType.SongSelect => "Song select",
            CueMidiCommandType.TuneRequest => "Tune request",
            CueMidiCommandType.TimingClock => "Timing clock",
            CueMidiCommandType.Start => "Start",
            CueMidiCommandType.Continue => "Continue",
            CueMidiCommandType.Stop => "Stop",
            CueMidiCommandType.ActiveSensing => "Active sensing",
            CueMidiCommandType.Reset => "Reset",

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
            HeadphonesCueTapPoint.PreFader => "Pre-fader",
            HeadphonesCueTapPoint.PostFader => "Post-fader",
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
            ControlScriptTriggerKind.MidiMessage => "MIDI message",
            ControlScriptTriggerKind.MidiControlChange => "MIDI control change",
            ControlScriptTriggerKind.MidiNote => "MIDI note",
            ControlScriptTriggerKind.OscMessage => "OSC message",
            ControlScriptTriggerKind.OscCacheChanged => "OSC cache changed",
            ControlScriptTriggerKind.LayerEnabled => "Layer enabled",
            ControlScriptTriggerKind.LayerDisabled => "Layer disabled",
            ControlScriptTriggerKind.Periodic => "Periodic",
            ControlScriptTriggerKind.Manual => "Manual",
            ControlMidiMessageType.Unknown => "Any MIDI message",
            ControlMidiMessageType.NRPN => "NRPN",
            ControlMidiMessageType.RPN => "RPN",
            ControlMidiMessageType.NoteOff => "Note off",
            ControlMidiMessageType.NoteOn => "Note on",
            ControlMidiMessageType.PolyphonicAftertouch => "Polyphonic aftertouch",
            ControlMidiMessageType.ControlChange => "Control change",
            ControlMidiMessageType.ProgramChange => "Program change",
            ControlMidiMessageType.ChannelAftertouch => "Channel aftertouch",
            ControlMidiMessageType.PitchBend => "Pitch bend",
            ControlMidiMessageType.SysEx => "SysEx",
            ControlMidiMessageType.MIDITimeCode => "MIDI time code",
            ControlMidiMessageType.SongPosition => "Song position",
            ControlMidiMessageType.SongSelect => "Song select",
            ControlMidiMessageType.TuneRequest => "Tune request",
            ControlMidiMessageType.TimingClock => "Timing clock",
            ControlMidiMessageType.Start => "Start",
            ControlMidiMessageType.Continue => "Continue",
            ControlMidiMessageType.Stop => "Stop",
            ControlMidiMessageType.ActiveSensing => "Active sensing",
            ControlMidiMessageType.Reset => "Reset",

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
