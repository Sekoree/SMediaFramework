using System.Text;

namespace S.Control;

public sealed class ControlDeviceMatcher
{
    public static ControlMIDIPortMatch MatchMIDIInput(
        ControlDeviceInstanceConfig device,
        IReadOnlyList<ControlMIDIPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ports);
        return MatchMIDIPort(
            ports,
            device.Binding.MIDIInputDeviceId,
            device.Binding.MIDIInputDeviceName,
            device.Binding.Alias,
            device.Name,
            "input");
    }

    public static ControlMIDIPortMatch MatchMIDIOutput(
        ControlDeviceInstanceConfig device,
        IReadOnlyList<ControlMIDIPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ports);
        return MatchMIDIPort(
            ports,
            device.Binding.MIDIOutputDeviceId,
            device.Binding.MIDIOutputDeviceName,
            device.Binding.Alias,
            device.Name,
            "output");
    }

    internal static ControlMIDIPortMatch MatchMIDIPort(
        IReadOnlyList<ControlMIDIPortInfo> ports,
        int? rememberedDeviceId,
        string? rememberedDeviceName,
        string? alias,
        string? deviceName,
        string direction)
    {
        ArgumentNullException.ThrowIfNull(ports);

        if (rememberedDeviceId is { } id)
        {
            var byId = ports.Where(p => p.Id == id).ToArray();
            if (byId.Length == 1)
                return ControlMIDIPortMatch.Matched(byId[0], ControlDeviceMatchKind.RememberedDeviceId);
        }

        var exactNameTerms = CandidateTerms(rememberedDeviceName, alias, deviceName).ToArray();
        var exactNameMatches = MatchByExactName(ports, exactNameTerms)
            .DistinctBy(m => m.Port.Id)
            .ToArray();
        if (exactNameMatches.Length == 1)
            return ControlMIDIPortMatch.Matched(exactNameMatches[0].Port, exactNameMatches[0].Kind);
        if (exactNameMatches.Length > 1)
            return ControlMIDIPortMatch.Ambiguous(
                exactNameMatches.Select(m => m.Port).DistinctBy(p => p.Id).ToArray(),
                $"MIDI {direction} device name is ambiguous and matches {exactNameMatches.Length} devices.");

        var fuzzyTerms = CandidateTerms(rememberedDeviceName, deviceName, alias).ToArray();
        var fuzzyMatches = MatchByFuzzyName(ports, fuzzyTerms).ToArray();
        if (fuzzyMatches.Length == 1)
            return ControlMIDIPortMatch.Matched(fuzzyMatches[0], ControlDeviceMatchKind.FuzzyName);
        if (fuzzyMatches.Length > 1)
            return ControlMIDIPortMatch.Ambiguous(
                fuzzyMatches,
                $"MIDI {direction} device name is ambiguous and fuzzy-matches {fuzzyMatches.Length} devices.");

        if (rememberedDeviceId is null && exactNameTerms.Length == 0)
            return ControlMIDIPortMatch.Unbound($"MIDI {direction} device is not bound.");

        var configured = rememberedDeviceName ?? alias ?? deviceName ?? rememberedDeviceId?.ToString() ?? "(unbound)";
        return ControlMIDIPortMatch.Missing($"MIDI {direction} device '{configured}' was not found.");
    }

    internal static bool IsFuzzyNameMatch(string? expected, string? actual)
    {
        var expectedNormalized = NormalizeName(expected);
        var actualNormalized = NormalizeName(actual);
        if (expectedNormalized.Length == 0 || actualNormalized.Length == 0)
            return false;
        if (string.Equals(expectedNormalized, actualNormalized, StringComparison.Ordinal))
            return true;
        if (actualNormalized.Contains(expectedNormalized, StringComparison.Ordinal)
            || expectedNormalized.Contains(actualNormalized, StringComparison.Ordinal))
        {
            return expectedNormalized.Length >= 4 || actualNormalized.Length >= 4;
        }

        var expectedTokens = TokenizeName(expected);
        var actualTokens = TokenizeName(actual);
        return expectedTokens.Count > 0
            && expectedTokens.All(token => actualTokens.Any(actualToken =>
                actualToken.Contains(token, StringComparison.Ordinal)
                || token.Contains(actualToken, StringComparison.Ordinal)));
    }

    private static IEnumerable<(ControlMIDIPortInfo Port, ControlDeviceMatchKind Kind)> MatchByExactName(
        IReadOnlyList<ControlMIDIPortInfo> ports,
        IReadOnlyList<ControlDeviceMatchTerm> terms)
    {
        foreach (var term in terms)
        {
            foreach (var port in ports)
            {
                if (string.Equals(port.Name, term.Value, StringComparison.OrdinalIgnoreCase))
                    yield return (port, term.Kind);
            }
        }
    }

    private static IEnumerable<ControlMIDIPortInfo> MatchByFuzzyName(
        IReadOnlyList<ControlMIDIPortInfo> ports,
        IReadOnlyList<ControlDeviceMatchTerm> terms)
    {
        foreach (var port in ports)
        {
            if (terms.Any(term => IsFuzzyNameMatch(term.Value, port.Name)))
                yield return port;
        }
    }

    private static IEnumerable<ControlDeviceMatchTerm> CandidateTerms(
        string? rememberedDeviceName,
        string? alias,
        string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(rememberedDeviceName))
            yield return new ControlDeviceMatchTerm(rememberedDeviceName, ControlDeviceMatchKind.ExactName);
        if (!string.IsNullOrWhiteSpace(alias))
            yield return new ControlDeviceMatchTerm(alias, ControlDeviceMatchKind.UserAlias);
        if (!string.IsNullOrWhiteSpace(deviceName))
            yield return new ControlDeviceMatchTerm(deviceName, ControlDeviceMatchKind.ExactName);
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> TokenizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([' ', '-', '_', '.', '/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeName)
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private sealed record ControlDeviceMatchTerm(string Value, ControlDeviceMatchKind Kind);
}

public enum ControlDeviceMatchStatus
{
    Unbound,
    Matched,
    Missing,
    Ambiguous,
}

public enum ControlDeviceMatchKind
{
    None,
    RememberedDeviceId,
    ExactName,
    UserAlias,
    FuzzyName,
}

public sealed record ControlMIDIPortInfo(int Id, string? Name);

public sealed record ControlMIDIPortMatch(
    ControlDeviceMatchStatus Status,
    ControlMIDIPortInfo? Port,
    ControlDeviceMatchKind Kind,
    IReadOnlyList<ControlMIDIPortInfo> Candidates,
    string Message)
{
    public bool IsMatched => Status == ControlDeviceMatchStatus.Matched && Port is not null;

    public static ControlMIDIPortMatch Matched(ControlMIDIPortInfo port, ControlDeviceMatchKind kind) =>
        new(ControlDeviceMatchStatus.Matched, port, kind, [port], string.Empty);

    public static ControlMIDIPortMatch Unbound(string message) =>
        new(ControlDeviceMatchStatus.Unbound, null, ControlDeviceMatchKind.None, [], message);

    public static ControlMIDIPortMatch Missing(string message) =>
        new(ControlDeviceMatchStatus.Missing, null, ControlDeviceMatchKind.None, [], message);

    public static ControlMIDIPortMatch Ambiguous(IReadOnlyList<ControlMIDIPortInfo> candidates, string message) =>
        new(ControlDeviceMatchStatus.Ambiguous, null, ControlDeviceMatchKind.None, candidates, message);
}
