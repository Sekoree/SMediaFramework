using OSCLib;

namespace S.Control;

/// <summary>
/// Maps incoming X32 <c>/meters</c> OSC blob replies into stable numeric cache addresses
/// such as <c>/meters/6/0</c>, <c>/meters/6/1</c>, …
/// </summary>
public static class X32MeterCacheDecoder
{
    public static IEnumerable<X32MeterCacheEntry> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob)
    {
        if (!string.Equals(oscAddress, "/meters", StringComparison.OrdinalIgnoreCase))
            yield break;

        var meterBase = ResolveMeterBasePath(arguments, blobArgumentIndex);
        if (meterBase is null)
            yield break;

        IReadOnlyList<float> values;
        try
        {
            values = X32Meters.ParseFloatBlob(blob).Values;
        }
        catch (FormatException)
        {
            try
            {
                values = X32Meters.ParseRtaDbBlob(blob);
            }
            catch (FormatException)
            {
                yield break;
            }
        }

        for (var i = 0; i < values.Count; i++)
            yield return new X32MeterCacheEntry($"{meterBase}/{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", values[i]);
    }

    private static string? ResolveMeterBasePath(IReadOnlyList<OSCArgument> arguments, int blobArgumentIndex)
    {
        if (blobArgumentIndex > 0)
        {
            var previous = arguments[blobArgumentIndex - 1];
            if (previous.Type is OSCArgumentType.String or OSCArgumentType.Symbol)
            {
                var path = previous.AsString();
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
        }

        foreach (var argument in arguments)
        {
            if (argument.Type is OSCArgumentType.String or OSCArgumentType.Symbol)
            {
                var path = argument.AsString();
                if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("/meters/", StringComparison.OrdinalIgnoreCase))
                    return path;
            }
        }

        return "/meters";
    }
}

public readonly record struct X32MeterCacheEntry(string Address, float Value);
