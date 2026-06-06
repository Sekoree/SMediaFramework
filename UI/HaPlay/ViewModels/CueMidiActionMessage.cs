using System.Globalization;
using PMLib.MessageTypes;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

internal readonly record struct CueMidiActionEditorState(
    CueMidiCommandType CommandType,
    int Channel,
    int Data1,
    int Data2,
    string DataText);

internal static class CueMidiActionMessage
{
    public const string DefaultSysExDataText = "F0 7D 00 F7";

    public static IReadOnlyList<CueMidiCommandType> CommandTypes { get; } =
        Enum.GetValues<CueMidiCommandType>();

    public static CueMidiActionEditorState Defaults(CueMidiCommandType type) =>
        type switch
        {
            CueMidiCommandType.NoteOn => new(type, 1, 60, 100, DefaultSysExDataText),
            CueMidiCommandType.NoteOff => new(type, 1, 60, 0, DefaultSysExDataText),
            CueMidiCommandType.PolyphonicAftertouch => new(type, 1, 60, 64, DefaultSysExDataText),
            CueMidiCommandType.ControlChange => new(type, 1, 1, 64, DefaultSysExDataText),
            CueMidiCommandType.HighResolutionControlChange => new(type, 1, 1, 8192, DefaultSysExDataText),
            CueMidiCommandType.ProgramChange => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.ChannelAftertouch => new(type, 1, 64, 0, DefaultSysExDataText),
            CueMidiCommandType.PitchBend => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.SysEx => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.MIDITimeCode => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.SongPosition => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.SongSelect => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMidiCommandType.NRPN or CueMidiCommandType.RPN => new(type, 1, 0, 8192, DefaultSysExDataText),
            _ => new(type, 1, 0, 0, DefaultSysExDataText),
        };

    public static bool UsesChannel(CueMidiCommandType type) =>
        type is CueMidiCommandType.NRPN
            or CueMidiCommandType.RPN
            or CueMidiCommandType.NoteOff
            or CueMidiCommandType.NoteOn
            or CueMidiCommandType.PolyphonicAftertouch
            or CueMidiCommandType.ControlChange
            or CueMidiCommandType.HighResolutionControlChange
            or CueMidiCommandType.ProgramChange
            or CueMidiCommandType.ChannelAftertouch
            or CueMidiCommandType.PitchBend;

    public static bool UsesData1(CueMidiCommandType type) =>
        type is CueMidiCommandType.NRPN
            or CueMidiCommandType.RPN
            or CueMidiCommandType.NoteOff
            or CueMidiCommandType.NoteOn
            or CueMidiCommandType.PolyphonicAftertouch
            or CueMidiCommandType.ControlChange
            or CueMidiCommandType.HighResolutionControlChange
            or CueMidiCommandType.ProgramChange
            or CueMidiCommandType.ChannelAftertouch
            or CueMidiCommandType.PitchBend
            or CueMidiCommandType.MIDITimeCode
            or CueMidiCommandType.SongPosition
            or CueMidiCommandType.SongSelect;

    public static bool UsesData2(CueMidiCommandType type) =>
        type is CueMidiCommandType.NRPN
            or CueMidiCommandType.RPN
            or CueMidiCommandType.NoteOff
            or CueMidiCommandType.NoteOn
            or CueMidiCommandType.PolyphonicAftertouch
            or CueMidiCommandType.ControlChange
            or CueMidiCommandType.HighResolutionControlChange;

    public static bool UsesDataText(CueMidiCommandType type) =>
        type == CueMidiCommandType.SysEx;

    public static string Data1Label(CueMidiCommandType type) =>
        type switch
        {
            CueMidiCommandType.NRPN or CueMidiCommandType.RPN => "Parameter",
            CueMidiCommandType.NoteOff or CueMidiCommandType.NoteOn or CueMidiCommandType.PolyphonicAftertouch => "Note",
            CueMidiCommandType.ControlChange or CueMidiCommandType.HighResolutionControlChange => "Controller",
            CueMidiCommandType.ProgramChange => "Program",
            CueMidiCommandType.ChannelAftertouch => "Pressure",
            CueMidiCommandType.PitchBend => "Bend",
            CueMidiCommandType.MIDITimeCode => "Data byte",
            CueMidiCommandType.SongPosition => "Beat",
            CueMidiCommandType.SongSelect => "Song",
            _ => "Value",
        };

    public static string Data2Label(CueMidiCommandType type) =>
        type switch
        {
            CueMidiCommandType.NRPN or CueMidiCommandType.RPN => "Value",
            CueMidiCommandType.NoteOff or CueMidiCommandType.NoteOn => "Velocity",
            CueMidiCommandType.PolyphonicAftertouch => "Pressure",
            CueMidiCommandType.ControlChange or CueMidiCommandType.HighResolutionControlChange => "Value",
            _ => "Value",
        };

    public static int Data1Minimum(CueMidiCommandType type) =>
        type == CueMidiCommandType.PitchBend ? -8192 : 0;

    public static int Data1Maximum(CueMidiCommandType type) =>
        type switch
        {
            CueMidiCommandType.NRPN or CueMidiCommandType.RPN or CueMidiCommandType.SongPosition => 16383,
            CueMidiCommandType.HighResolutionControlChange => 31,
            CueMidiCommandType.PitchBend => 8191,
            _ => 127,
        };

    public static int Data2Minimum(CueMidiCommandType type) => 0;

    public static int Data2Maximum(CueMidiCommandType type) =>
        type is CueMidiCommandType.NRPN
            or CueMidiCommandType.RPN
            or CueMidiCommandType.HighResolutionControlChange
            ? 16383
            : 127;

    public static string BuildCommandText(
        CueMidiCommandType type,
        int channel,
        int data1,
        int data2,
        string? dataText)
    {
        var prefix = UsesChannel(type)
            ? $"ch{Math.Clamp(channel, 1, 16).ToString(CultureInfo.InvariantCulture)} "
            : string.Empty;

        var d1 = Math.Clamp(data1, Data1Minimum(type), Data1Maximum(type));
        var d2 = Math.Clamp(data2, Data2Minimum(type), Data2Maximum(type));
        var command = CommandToken(type);

        return type switch
        {
            CueMidiCommandType.NoteOn
                or CueMidiCommandType.NoteOff
                or CueMidiCommandType.PolyphonicAftertouch
                or CueMidiCommandType.ControlChange
                or CueMidiCommandType.HighResolutionControlChange
                or CueMidiCommandType.NRPN
                or CueMidiCommandType.RPN => $"{prefix}{command} {d1} {d2}",
            CueMidiCommandType.ProgramChange
                or CueMidiCommandType.ChannelAftertouch
                or CueMidiCommandType.PitchBend
                or CueMidiCommandType.MIDITimeCode
                or CueMidiCommandType.SongPosition
                or CueMidiCommandType.SongSelect => $"{prefix}{command} {d1}",
            CueMidiCommandType.SysEx => $"{command} {NormalizeSysExText(dataText)}",
            _ => command,
        };
    }

    public static CueMidiActionEditorState ParseEditorState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Defaults(CueMidiCommandType.NoteOn);

        var tokens = SplitTokens(raw);
        if (tokens.Count == 0)
            return Defaults(CueMidiCommandType.NoteOn);

        var idx = 0;
        var channel = 1;
        if (TryParseChannelPrefix(tokens[idx], out var parsedChannel))
        {
            channel = parsedChannel;
            idx++;
        }

        if (idx >= tokens.Count)
            return Defaults(CueMidiCommandType.NoteOn) with { Channel = channel };

        var type = ParseCommandToken(tokens[idx++]);
        var defaults = Defaults(type) with { Channel = channel };

        if (type == CueMidiCommandType.SysEx)
            return defaults with { DataText = idx < tokens.Count ? string.Join(' ', tokens.Skip(idx)) : DefaultSysExDataText };

        var d1 = defaults.Data1;
        var d2 = defaults.Data2;
        if (UsesData1(type) && idx < tokens.Count && int.TryParse(tokens[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedData1))
        {
            d1 = Math.Clamp(parsedData1, Data1Minimum(type), Data1Maximum(type));
            idx++;
        }

        if (UsesData2(type) && idx < tokens.Count && int.TryParse(tokens[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedData2))
            d2 = Math.Clamp(parsedData2, Data2Minimum(type), Data2Maximum(type));

        return defaults with { Data1 = d1, Data2 = d2 };
    }

    public static (IMIDIMessage Message, string Description) CreateMessage(string raw, int defaultZeroBasedChannel)
    {
        var tokens = SplitTokens(raw);
        if (tokens.Count == 0)
            throw new InvalidOperationException(Strings.MidiActionRequiresMessage);

        var idx = 0;
        var channel = Math.Clamp(defaultZeroBasedChannel, 0, 15);
        if (TryParseChannelPrefix(tokens[idx], out var parsedChannel))
        {
            channel = Math.Clamp(parsedChannel - 1, 0, 15);
            idx++;
        }

        if (idx >= tokens.Count)
            throw new InvalidOperationException(Strings.MidiActionCommandMissing);

        var commandText = tokens[idx++];
        var type = ParseCommandToken(commandText);
        IMIDIMessage message = type switch
        {
            CueMidiCommandType.NoteOn => new NoteOn(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), ParseSevenBit(tokens, idx++, "velocity")),
            CueMidiCommandType.NoteOff => new NoteOff(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), idx < tokens.Count ? ParseSevenBit(tokens, idx++, "velocity") : (byte)0),
            CueMidiCommandType.ControlChange => new ControlChange(ToChannel(channel), ParseSevenBit(tokens, idx++, "controller"), ParseSevenBit(tokens, idx++, "value")),
            CueMidiCommandType.HighResolutionControlChange => ControlChange.HighRes(ToChannel(channel), ParseHighResController(tokens, idx++), ParseFourteenBit(tokens, idx++, "value")),
            CueMidiCommandType.ProgramChange => new ProgramChange(ToChannel(channel), ParseSevenBit(tokens, idx++, "program")),
            CueMidiCommandType.PolyphonicAftertouch => new PolyphonicAftertouch(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), ParseSevenBit(tokens, idx++, "pressure")),
            CueMidiCommandType.ChannelAftertouch => new ChannelAftertouch(ToChannel(channel), ParseSevenBit(tokens, idx++, "pressure")),
            CueMidiCommandType.PitchBend => new PitchBend(ToChannel(channel), ParsePitchBend(tokens, idx++, "bend")),
            CueMidiCommandType.SysEx => new SysEx(ParseSysExBytes(tokens.Skip(idx))),
            CueMidiCommandType.MIDITimeCode => new MIDITimeCode(ParseSevenBit(tokens, idx++, "data byte")),
            CueMidiCommandType.SongPosition => new SongPosition(ParseFourteenBit(tokens, idx++, "beat")),
            CueMidiCommandType.SongSelect => new SongSelect(ParseSevenBit(tokens, idx++, "song")),
            CueMidiCommandType.TuneRequest => new TuneRequest(),
            CueMidiCommandType.TimingClock => new TimingClock(),
            CueMidiCommandType.Start => new MIDIStart(),
            CueMidiCommandType.Continue => new MIDIContinue(),
            CueMidiCommandType.Stop => new MIDIStop(),
            CueMidiCommandType.ActiveSensing => new ActiveSensing(),
            CueMidiCommandType.Reset => new MIDIReset(),
            CueMidiCommandType.NRPN => new NRPN(ToChannel(channel), ParseFourteenBit(tokens, idx++, "parameter"), ParseFourteenBit(tokens, idx++, "value")),
            CueMidiCommandType.RPN => new RPN(ToChannel(channel), ParseFourteenBit(tokens, idx++, "parameter"), ParseFourteenBit(tokens, idx++, "value")),
            _ => throw new InvalidOperationException(Strings.Format(nameof(Strings.UnsupportedMidiCommandFormat), commandText)),
        };

        var description = UsesChannel(type)
            ? Strings.Format(nameof(Strings.MidiSpecDescriptionFormat), CommandToken(type), channel + 1)
            : CommandToken(type);
        return (message, description);
    }

    private static string CommandToken(CueMidiCommandType type) =>
        type switch
        {
            CueMidiCommandType.NoteOn => "noteon",
            CueMidiCommandType.NoteOff => "noteoff",
            CueMidiCommandType.ControlChange => "cc",
            CueMidiCommandType.HighResolutionControlChange => "cc14",
            CueMidiCommandType.ProgramChange => "pc",
            CueMidiCommandType.PolyphonicAftertouch => "polyaftertouch",
            CueMidiCommandType.ChannelAftertouch => "aftertouch",
            CueMidiCommandType.PitchBend => "pitchbend",
            CueMidiCommandType.SysEx => "sysex",
            CueMidiCommandType.MIDITimeCode => "mtc",
            CueMidiCommandType.SongPosition => "songpos",
            CueMidiCommandType.SongSelect => "songselect",
            CueMidiCommandType.TuneRequest => "tunerequest",
            CueMidiCommandType.TimingClock => "clock",
            CueMidiCommandType.Start => "start",
            CueMidiCommandType.Continue => "continue",
            CueMidiCommandType.Stop => "stop",
            CueMidiCommandType.ActiveSensing => "activesensing",
            CueMidiCommandType.Reset => "reset",
            CueMidiCommandType.NRPN => "nrpn",
            CueMidiCommandType.RPN => "rpn",
            _ => "noteon",
        };

    private static CueMidiCommandType ParseCommandToken(string token)
    {
        var normalized = token.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "noteon" or "on" => CueMidiCommandType.NoteOn,
            "noteoff" or "off" => CueMidiCommandType.NoteOff,
            "cc" or "controlchange" => CueMidiCommandType.ControlChange,
            "cc14" or "highrescc" or "highresolutioncc" or "highresolutioncontrolchange" => CueMidiCommandType.HighResolutionControlChange,
            "pc" or "program" or "programchange" => CueMidiCommandType.ProgramChange,
            "polyaftertouch" or "polyphonicaftertouch" or "polypressure" => CueMidiCommandType.PolyphonicAftertouch,
            "aftertouch" or "channelaftertouch" or "channelpressure" => CueMidiCommandType.ChannelAftertouch,
            "pitchbend" or "bend" => CueMidiCommandType.PitchBend,
            "sysex" or "systemexclusive" => CueMidiCommandType.SysEx,
            "mtc" or "miditimecode" => CueMidiCommandType.MIDITimeCode,
            "songpos" or "songposition" or "songpositionpointer" => CueMidiCommandType.SongPosition,
            "songselect" or "song" => CueMidiCommandType.SongSelect,
            "tunerequest" or "tune" => CueMidiCommandType.TuneRequest,
            "clock" or "timingclock" => CueMidiCommandType.TimingClock,
            "start" => CueMidiCommandType.Start,
            "continue" => CueMidiCommandType.Continue,
            "stop" => CueMidiCommandType.Stop,
            "activesensing" => CueMidiCommandType.ActiveSensing,
            "reset" => CueMidiCommandType.Reset,
            "nrpn" => CueMidiCommandType.NRPN,
            "rpn" => CueMidiCommandType.RPN,
            _ => throw new InvalidOperationException(Strings.Format(nameof(Strings.UnsupportedMidiCommandFormat), token)),
        };
    }

    private static List<string> SplitTokens(string raw) =>
        raw.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool TryParseChannelPrefix(string token, out int channel)
    {
        channel = 1;
        if (!token.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = token.AsSpan(token.Contains('=') ? token.IndexOf('=') + 1 : 2);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        channel = Math.Clamp(parsed, 1, 16);
        return true;
    }

    private static byte ToChannel(int zeroBasedChannel) =>
        (byte)Math.Clamp(zeroBasedChannel, 0, 15);

    private static byte ParseSevenBit(IReadOnlyList<string> tokens, int index, string name)
    {
        var value = ParseInt(tokens, index, name);
        if (value is < 0 or > 127)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), name, tokens[index]));
        return (byte)value;
    }

    private static byte ParseHighResController(IReadOnlyList<string> tokens, int index)
    {
        var value = ParseInt(tokens, index, "controller");
        if (value is < 0 or > 31)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), "controller", tokens[index]));
        return (byte)value;
    }

    private static ushort ParseFourteenBit(IReadOnlyList<string> tokens, int index, string name)
    {
        var value = ParseInt(tokens, index, name);
        if (value is < 0 or > 16383)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), name, tokens[index]));
        return (ushort)value;
    }

    private static int ParsePitchBend(IReadOnlyList<string> tokens, int index, string name)
    {
        var value = ParseInt(tokens, index, name);
        if (value is < -8192 or > 8191)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), name, tokens[index]));
        return value;
    }

    private static int ParseInt(IReadOnlyList<string> tokens, int index, string name)
    {
        if (index < 0 || index >= tokens.Count)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentMissingFormat), name));
        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), name, tokens[index]));
        return value;
    }

    private static byte[] ParseSysExBytes(IEnumerable<string> rawTokens)
    {
        var tokens = rawTokens.ToArray();
        if (tokens.Length == 0)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentMissingFormat), "SysEx data"));

        var bytes = new List<byte>();
        foreach (var token in tokens)
        {
            var cleaned = token.Trim();
            if (cleaned.Length == 0)
                continue;

            var hex = cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? cleaned[2..]
                : cleaned;
            if (hex.Length > 2 && hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
            {
                for (var i = 0; i < hex.Length; i += 2)
                    bytes.Add(byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                continue;
            }

            var style = cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || cleaned.Any(c => char.IsAsciiHexDigit(c) && char.IsLetter(c))
                ? NumberStyles.HexNumber
                : NumberStyles.Integer;
            if (!byte.TryParse(hex, style, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), "SysEx byte", token));
            bytes.Add(value);
        }

        if (bytes.Count == 0)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentMissingFormat), "SysEx data"));

        if (bytes[0] != 0xF0)
            bytes.Insert(0, 0xF0);
        if (bytes[^1] != 0xF7)
            bytes.Add(0xF7);
        return bytes.ToArray();
    }

    private static string NormalizeSysExText(string? dataText)
    {
        var text = string.IsNullOrWhiteSpace(dataText) ? DefaultSysExDataText : dataText.Trim();
        return string.Join(' ', ParseSysExBytes(SplitTokens(text)).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
