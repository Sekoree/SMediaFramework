namespace HaPlay.ControlGraph;

/// <summary>
/// Shared OSC address matching used by script triggers and cache overrides. Supports an exact
/// match, a catch-all (<c>null</c>/empty/<c>"*"</c>), and a single <c>*</c> wildcard that matches any
/// run of characters between a fixed prefix and suffix.
/// </summary>
public static class ControlOscAddressPattern
{
    public static bool Matches(string? pattern, string address)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;
        if (string.Equals(pattern, address, StringComparison.Ordinal))
            return true;

        var starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
        if (starIndex < 0)
            return false;

        var prefix = pattern[..starIndex];
        var suffix = pattern[(starIndex + 1)..];
        return address.StartsWith(prefix, StringComparison.Ordinal)
            && address.EndsWith(suffix, StringComparison.Ordinal);
    }
}
