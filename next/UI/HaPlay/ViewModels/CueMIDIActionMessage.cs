using System.Globalization;
using PMLib.MessageTypes;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

internal readonly record struct CueMIDIActionEditorState(
    CueMIDICommandType CommandType,
    int Channel,
    int Data1,
    int Data2,
    string DataText);

internal static class CueMIDIActionMessage
{
    public const string DefaultSysExDataText = "F0 7D 00 F7";

    public static IReadOnlyList<CueMIDICommandType> CommandTypes { get; } =
        Enum.GetValues<CueMIDICommandType>();

    public static CueMIDIActionEditorState Defaults(CueMIDICommandType type) =>
        type switch
        {
            CueMIDICommandType.NoteOn => new(type, 1, 60, 100, DefaultSysExDataText),
            CueMIDICommandType.NoteOff => new(type, 1, 60, 0, DefaultSysExDataText),
            CueMIDICommandType.PolyphonicAftertouch => new(type, 1, 60, 64, DefaultSysExDataText),
            CueMIDICommandType.ControlChange => new(type, 1, 1, 64, DefaultSysExDataText),
            CueMIDICommandType.HighResolutionControlChange => new(type, 1, 1, 8192, DefaultSysExDataText),
            CueMIDICommandType.ProgramChange => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.ChannelAftertouch => new(type, 1, 64, 0, DefaultSysExDataText),
            CueMIDICommandType.PitchBend => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.SysEx => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.MIDITimeCode => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.SongPosition => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.SongSelect => new(type, 1, 0, 0, DefaultSysExDataText),
            CueMIDICommandType.NRPN or CueMIDICommandType.RPN => new(type, 1, 0, 8192, DefaultSysExDataText),
            _ => new(type, 1, 0, 0, DefaultSysExDataText),
        };

    public static bool UsesChannel(CueMIDICommandType type) =>
        type is CueMIDICommandType.NRPN
            or CueMIDICommandType.RPN
            or CueMIDICommandType.NoteOff
            or CueMIDICommandType.NoteOn
            or CueMIDICommandType.PolyphonicAftertouch
            or CueMIDICommandType.ControlChange
            or CueMIDICommandType.HighResolutionControlChange
            or CueMIDICommandType.ProgramChange
            or CueMIDICommandType.ChannelAftertouch
            or CueMIDICommandType.PitchBend;

    public static bool UsesData1(CueMIDICommandType type) =>
        type is CueMIDICommandType.NRPN
            or CueMIDICommandType.RPN
            or CueMIDICommandType.NoteOff
            or CueMIDICommandType.NoteOn
            or CueMIDICommandType.PolyphonicAftertouch
            or CueMIDICommandType.ControlChange
            or CueMIDICommandType.HighResolutionControlChange
            or CueMIDICommandType.ProgramChange
            or CueMIDICommandType.ChannelAftertouch
            or CueMIDICommandType.PitchBend
            or CueMIDICommandType.MIDITimeCode
            or CueMIDICommandType.SongPosition
            or CueMIDICommandType.SongSelect;

    public static bool UsesData2(CueMIDICommandType type) =>
        type is CueMIDICommandType.NRPN
            or CueMIDICommandType.RPN
            or CueMIDICommandType.NoteOff
            or CueMIDICommandType.NoteOn
            or CueMIDICommandType.PolyphonicAftertouch
            or CueMIDICommandType.ControlChange
            or CueMIDICommandType.HighResolutionControlChange;

    public static bool UsesDataText(CueMIDICommandType type) =>
        type == CueMIDICommandType.SysEx;

    public static string Data1Label(CueMIDICommandType type) =>
        type switch
        {
            CueMIDICommandType.NRPN or CueMIDICommandType.RPN => "Parameter",
            CueMIDICommandType.NoteOff or CueMIDICommandType.NoteOn or CueMIDICommandType.PolyphonicAftertouch => "Note",
            CueMIDICommandType.ControlChange or CueMIDICommandType.HighResolutionControlChange => "Controller",
            CueMIDICommandType.ProgramChange => "Program",
            CueMIDICommandType.ChannelAftertouch => "Pressure",
            CueMIDICommandType.PitchBend => "Bend",
            CueMIDICommandType.MIDITimeCode => "Data byte",
            CueMIDICommandType.SongPosition => "Beat",
            CueMIDICommandType.SongSelect => "Song",
            _ => "Value",
        };

    public static string Data2Label(CueMIDICommandType type) =>
        type switch
        {
            CueMIDICommandType.NRPN or CueMIDICommandType.RPN => "Value",
            CueMIDICommandType.NoteOff or CueMIDICommandType.NoteOn => "Velocity",
            CueMIDICommandType.PolyphonicAftertouch => "Pressure",
            CueMIDICommandType.ControlChange or CueMIDICommandType.HighResolutionControlChange => "Value",
            _ => "Value",
        };

    public static int Data1Minimum(CueMIDICommandType type) =>
        type == CueMIDICommandType.PitchBend ? -8192 : 0;

    public static int Data1Maximum(CueMIDICommandType type) =>
        type switch
        {
            CueMIDICommandType.NRPN or CueMIDICommandType.RPN or CueMIDICommandType.SongPosition => 16383,
            CueMIDICommandType.HighResolutionControlChange => 31,
            CueMIDICommandType.PitchBend => 8191,
            _ => 127,
        };

    public static int Data2Minimum(CueMIDICommandType type) => 0;

    public static int Data2Maximum(CueMIDICommandType type) =>
        type is CueMIDICommandType.NRPN
            or CueMIDICommandType.RPN
            or CueMIDICommandType.HighResolutionControlChange
            ? 16383
            : 127;

    public static string BuildCommandText(
        CueMIDICommandType type,
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
            CueMIDICommandType.NoteOn
                or CueMIDICommandType.NoteOff
                or CueMIDICommandType.PolyphonicAftertouch
                or CueMIDICommandType.ControlChange
                or CueMIDICommandType.HighResolutionControlChange
                or CueMIDICommandType.NRPN
                or CueMIDICommandType.RPN => $"{prefix}{command} {d1} {d2}",
            CueMIDICommandType.ProgramChange
                or CueMIDICommandType.ChannelAftertouch
                or CueMIDICommandType.PitchBend
                or CueMIDICommandType.MIDITimeCode
                or CueMIDICommandType.SongPosition
                or CueMIDICommandType.SongSelect => $"{prefix}{command} {d1}",
            CueMIDICommandType.SysEx => $"{command} {NormalizeSysExText(dataText)}",
            _ => command,
        };
    }

    public static CueMIDIActionEditorState ParseEditorState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Defaults(CueMIDICommandType.NoteOn);

        var tokens = SplitTokens(raw);
        if (tokens.Count == 0)
            return Defaults(CueMIDICommandType.NoteOn);

        var idx = 0;
        var channel = 1;
        if (TryParseChannelPrefix(tokens[idx], out var parsedChannel))
        {
            channel = parsedChannel;
            idx++;
        }

        if (idx >= tokens.Count)
            return Defaults(CueMIDICommandType.NoteOn) with { Channel = channel };

        var type = ParseCommandToken(tokens[idx++]);
        var defaults = Defaults(type) with { Channel = channel };

        if (type == CueMIDICommandType.SysEx)
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
            throw new InvalidOperationException(Strings.MIDIActionRequiresMessage);

        var idx = 0;
        var channel = Math.Clamp(defaultZeroBasedChannel, 0, 15);
        if (TryParseChannelPrefix(tokens[idx], out var parsedChannel))
        {
            channel = Math.Clamp(parsedChannel - 1, 0, 15);
            idx++;
        }

        if (idx >= tokens.Count)
            throw new InvalidOperationException(Strings.MIDIActionCommandMissing);

        var commandText = tokens[idx++];
        var type = ParseCommandToken(commandText);
        IMIDIMessage message = type switch
        {
            CueMIDICommandType.NoteOn => new NoteOn(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), ParseSevenBit(tokens, idx++, "velocity")),
            CueMIDICommandType.NoteOff => new NoteOff(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), idx < tokens.Count ? ParseSevenBit(tokens, idx++, "velocity") : (byte)0),
            CueMIDICommandType.ControlChange => new ControlChange(ToChannel(channel), ParseSevenBit(tokens, idx++, "controller"), ParseSevenBit(tokens, idx++, "value")),
            CueMIDICommandType.HighResolutionControlChange => ControlChange.HighRes(ToChannel(channel), ParseHighResController(tokens, idx++), ParseFourteenBit(tokens, idx++, "value")),
            CueMIDICommandType.ProgramChange => new ProgramChange(ToChannel(channel), ParseSevenBit(tokens, idx++, "program")),
            CueMIDICommandType.PolyphonicAftertouch => new PolyphonicAftertouch(ToChannel(channel), ParseSevenBit(tokens, idx++, "note"), ParseSevenBit(tokens, idx++, "pressure")),
            CueMIDICommandType.ChannelAftertouch => new ChannelAftertouch(ToChannel(channel), ParseSevenBit(tokens, idx++, "pressure")),
            CueMIDICommandType.PitchBend => new PitchBend(ToChannel(channel), ParsePitchBend(tokens, idx++, "bend")),
            CueMIDICommandType.SysEx => new SysEx(ParseSysExBytes(tokens.Skip(idx))),
            CueMIDICommandType.MIDITimeCode => new MIDITimeCode(ParseSevenBit(tokens, idx++, "data byte")),
            CueMIDICommandType.SongPosition => new SongPosition(ParseFourteenBit(tokens, idx++, "beat")),
            CueMIDICommandType.SongSelect => new SongSelect(ParseSevenBit(tokens, idx++, "song")),
            CueMIDICommandType.TuneRequest => new TuneRequest(),
            CueMIDICommandType.TimingClock => new TimingClock(),
            CueMIDICommandType.Start => new MIDIStart(),
            CueMIDICommandType.Continue => new MIDIContinue(),
            CueMIDICommandType.Stop => new MIDIStop(),
            CueMIDICommandType.ActiveSensing => new ActiveSensing(),
            CueMIDICommandType.Reset => new MIDIReset(),
            CueMIDICommandType.NRPN => new NRPN(ToChannel(channel), ParseFourteenBit(tokens, idx++, "parameter"), ParseFourteenBit(tokens, idx++, "value")),
            CueMIDICommandType.RPN => new RPN(ToChannel(channel), ParseFourteenBit(tokens, idx++, "parameter"), ParseFourteenBit(tokens, idx++, "value")),
            _ => throw new InvalidOperationException(Strings.Format(nameof(Strings.UnsupportedMIDICommandFormat), commandText)),
        };

        var description = UsesChannel(type)
            ? Strings.Format(nameof(Strings.MIDISpecDescriptionFormat), CommandToken(type), channel + 1)
            : CommandToken(type);
        return (message, description);
    }

    private static string CommandToken(CueMIDICommandType type) =>
        type switch
        {
            CueMIDICommandType.NoteOn => "noteon",
            CueMIDICommandType.NoteOff => "noteoff",
            CueMIDICommandType.ControlChange => "cc",
            CueMIDICommandType.HighResolutionControlChange => "cc14",
            CueMIDICommandType.ProgramChange => "pc",
            CueMIDICommandType.PolyphonicAftertouch => "polyaftertouch",
            CueMIDICommandType.ChannelAftertouch => "aftertouch",
            CueMIDICommandType.PitchBend => "pitchbend",
            CueMIDICommandType.SysEx => "sysex",
            CueMIDICommandType.MIDITimeCode => "mtc",
            CueMIDICommandType.SongPosition => "songpos",
            CueMIDICommandType.SongSelect => "songselect",
            CueMIDICommandType.TuneRequest => "tunerequest",
            CueMIDICommandType.TimingClock => "clock",
            CueMIDICommandType.Start => "start",
            CueMIDICommandType.Continue => "continue",
            CueMIDICommandType.Stop => "stop",
            CueMIDICommandType.ActiveSensing => "activesensing",
            CueMIDICommandType.Reset => "reset",
            CueMIDICommandType.NRPN => "nrpn",
            CueMIDICommandType.RPN => "rpn",
            _ => "noteon",
        };

    private static CueMIDICommandType ParseCommandToken(string token)
    {
        var normalized = token.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "noteon" or "on" => CueMIDICommandType.NoteOn,
            "noteoff" or "off" => CueMIDICommandType.NoteOff,
            "cc" or "controlchange" => CueMIDICommandType.ControlChange,
            "cc14" or "highrescc" or "highresolutioncc" or "highresolutioncontrolchange" => CueMIDICommandType.HighResolutionControlChange,
            "pc" or "program" or "programchange" => CueMIDICommandType.ProgramChange,
            "polyaftertouch" or "polyphonicaftertouch" or "polypressure" => CueMIDICommandType.PolyphonicAftertouch,
            "aftertouch" or "channelaftertouch" or "channelpressure" => CueMIDICommandType.ChannelAftertouch,
            "pitchbend" or "bend" => CueMIDICommandType.PitchBend,
            "sysex" or "systemexclusive" => CueMIDICommandType.SysEx,
            "mtc" or "miditimecode" => CueMIDICommandType.MIDITimeCode,
            "songpos" or "songposition" or "songpositionpointer" => CueMIDICommandType.SongPosition,
            "songselect" or "song" => CueMIDICommandType.SongSelect,
            "tunerequest" or "tune" => CueMIDICommandType.TuneRequest,
            "clock" or "timingclock" => CueMIDICommandType.TimingClock,
            "start" => CueMIDICommandType.Start,
            "continue" => CueMIDICommandType.Continue,
            "stop" => CueMIDICommandType.Stop,
            "activesensing" => CueMIDICommandType.ActiveSensing,
            "reset" => CueMIDICommandType.Reset,
            "nrpn" => CueMIDICommandType.NRPN,
            "rpn" => CueMIDICommandType.RPN,
            _ => throw new InvalidOperationException(Strings.Format(nameof(Strings.UnsupportedMIDICommandFormat), token)),
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
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), name, tokens[index]));
        return (byte)value;
    }

    private static byte ParseHighResController(IReadOnlyList<string> tokens, int index)
    {
        var value = ParseInt(tokens, index, "controller");
        if (value is < 0 or > 31)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), "controller", tokens[index]));
        return (byte)value;
    }

    private static ushort ParseFourteenBit(IReadOnlyList<string> tokens, int index, string name)
    {
        var value = ParseInt(tokens, index, name);
        if (value is < 0 or > 16383)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), name, tokens[index]));
        return (ushort)value;
    }

    private static int ParsePitchBend(IReadOnlyList<string> tokens, int index, string name)
    {
        var value = ParseInt(tokens, index, name);
        if (value is < -8192 or > 8191)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), name, tokens[index]));
        return value;
    }

    private static int ParseInt(IReadOnlyList<string> tokens, int index, string name)
    {
        if (index < 0 || index >= tokens.Count)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentMissingFormat), name));
        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), name, tokens[index]));
        return value;
    }

    private static byte[] ParseSysExBytes(IEnumerable<string> rawTokens)
    {
        var tokens = rawTokens.ToArray();
        if (tokens.Length == 0)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentMissingFormat), "SysEx data"));

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
                throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentInvalidFormat), "SysEx byte", token));
            bytes.Add(value);
        }

        if (bytes.Count == 0)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MIDIArgumentMissingFormat), "SysEx data"));

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
