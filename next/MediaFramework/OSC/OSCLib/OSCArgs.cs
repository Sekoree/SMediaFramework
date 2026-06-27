namespace OSCLib;

public static class OSCArgs
{
    public static OSCArgument I32(int value) => OSCArgument.Int32(value);

    public static OSCArgument F32(float value) => OSCArgument.Float32(value);

    public static OSCArgument Str(string value) => OSCArgument.String(value);

    public static OSCArgument Blob(ReadOnlyMemory<byte> value) => OSCArgument.Blob(value);

    public static OSCArgument I64(long value) => OSCArgument.Int64(value);

    public static OSCArgument Time(OSCTimeTag value) => OSCArgument.TimeTag(value);

    public static OSCArgument F64(double value) => OSCArgument.Double64(value);

    public static OSCArgument Symbol(string value) => OSCArgument.Symbol(value);

    public static OSCArgument Char(char value) => OSCArgument.Char(value);

    public static OSCArgument Color(uint rgba) => OSCArgument.RgbaColor(rgba);

    public static OSCArgument MIDI(byte portId, byte status, byte data1, byte data2)
        => OSCArgument.MIDI(new OSCMIDIMessage(portId, status, data1, data2));

    public static OSCArgument True() => OSCArgument.True();

    public static OSCArgument False() => OSCArgument.False();

    public static OSCArgument Nil() => OSCArgument.Nil();

    public static OSCArgument Impulse() => OSCArgument.Impulse();

    public static OSCArgument Array(params OSCArgument[] values) => OSCArgument.Array(values);

    public static OSCArgument Unknown(char tag, ReadOnlyMemory<byte> rawData)
        => OSCArgument.Unknown(tag, rawData);
}
