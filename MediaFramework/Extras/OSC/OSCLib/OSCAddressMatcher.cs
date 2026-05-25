using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace OSCLib;

public static class OSCAddressMatcher
{
    // Bounded cache for the public IsMatch API. OSCRouter pre-compiles patterns at
    // Register time via Compile() and never touches this cache on the hot dispatch path.
    private static readonly ConcurrentDictionary<string, Regex> PartRegexCache = new(StringComparer.Ordinal);
    private const int PartRegexCacheMaxEntries = 256;

    public static bool IsMatch(string pattern, string address)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(address))
            return false;

        if (pattern[0] != '/' || address[0] != '/')
            return false;

        var patternParts = Split(pattern);
        var addressParts = Split(address);

        return MatchParts(patternParts, 0, addressParts, 0);
    }

    /// <summary>
    /// Pre-compiles all per-segment regexes for <paramref name="pattern"/> and returns a
    /// reusable delegate that matches an address string without any further allocations.
    /// Called once at route-registration time by <see cref="OSCRouter"/>.
    /// </summary>
    internal static Func<string, bool> Compile(string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern[0] != '/')
            return static _ => false;

        var patternParts = Split(pattern);
        // null entry = '//' wildcard that matches zero-or-more path segments
        var compiledParts = patternParts
            .Select(p => p.Length > 0 ? BuildPartRegex(p) : null)
            .ToArray();

        return address =>
        {
            if (string.IsNullOrEmpty(address) || address[0] != '/')
                return false;
            var addressParts = Split(address);
            return MatchCompiledParts(compiledParts, 0, addressParts, 0);
        };
    }

    // -----------------------------------------------------------------------
    // Public path (uses bounded cache)
    // -----------------------------------------------------------------------

    private static bool MatchParts(string[] patternParts, int p, string[] addressParts, int a)
    {
        if (p == patternParts.Length)
            return a == addressParts.Length;

        if (patternParts[p].Length == 0)
        {
            for (var i = a; i <= addressParts.Length; i++)
            {
                if (MatchParts(patternParts, p + 1, addressParts, i))
                    return true;
            }

            return false;
        }

        if (a >= addressParts.Length)
            return false;

        if (!MatchPart(patternParts[p], addressParts[a]))
            return false;

        return MatchParts(patternParts, p + 1, addressParts, a + 1);
    }

    private static bool MatchPart(string patternPart, string addressPart)
    {
        if (PartRegexCache.TryGetValue(patternPart, out var cached))
            return cached.IsMatch(addressPart);

        var regex = BuildPartRegex(patternPart);
        if (PartRegexCache.Count < PartRegexCacheMaxEntries)
            PartRegexCache.TryAdd(patternPart, regex);

        return regex.IsMatch(addressPart);
    }

    // -----------------------------------------------------------------------
    // Compile path (pre-compiled, no cache lookup on hot path)
    // -----------------------------------------------------------------------

    private static bool MatchCompiledParts(Regex?[] compiledParts, int p, string[] addressParts, int a)
    {
        if (p == compiledParts.Length)
            return a == addressParts.Length;

        if (compiledParts[p] is null) // '//' cross-segment wildcard
        {
            for (var i = a; i <= addressParts.Length; i++)
            {
                if (MatchCompiledParts(compiledParts, p + 1, addressParts, i))
                    return true;
            }

            return false;
        }

        if (a >= addressParts.Length)
            return false;

        if (!compiledParts[p]!.IsMatch(addressParts[a]))
            return false;

        return MatchCompiledParts(compiledParts, p + 1, addressParts, a + 1);
    }

    // -----------------------------------------------------------------------
    // Shared regex construction
    // -----------------------------------------------------------------------

    private static Regex BuildPartRegex(string patternPart)
    {
        var sb = new StringBuilder();
        sb.Append('^');

        for (var i = 0; i < patternPart.Length; i++)
        {
            var ch = patternPart[i];
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    i = AppendBracketPattern(patternPart, i, sb);
                    break;
                case '{':
                    i = AppendAlternationPattern(patternPart, i, sb);
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static int AppendBracketPattern(string patternPart, int startIndex, StringBuilder sb)
    {
        var close = patternPart.IndexOf(']', startIndex + 1);
        if (close < 0)
        {
            sb.Append("\\[");
            return startIndex;
        }

        var content = patternPart.Substring(startIndex + 1, close - startIndex - 1);
        var negate = content.Length > 0 && content[0] == '!';
        var body = negate ? content[1..] : content;

        body = body.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("]", "\\]", StringComparison.Ordinal);
        if (!negate && body.StartsWith('^'))
            body = "\\" + body;

        sb.Append(negate ? "[^" : "[").Append(body).Append(']');
        return close;
    }

    private static int AppendAlternationPattern(string patternPart, int startIndex, StringBuilder sb)
    {
        var close = patternPart.IndexOf('}', startIndex + 1);
        if (close < 0)
        {
            sb.Append("\\{");
            return startIndex;
        }

        var content = patternPart.Substring(startIndex + 1, close - startIndex - 1);
        var options = content.Split(',', StringSplitOptions.None);
        sb.Append("(?:");
        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0)
                sb.Append('|');
            sb.Append(Regex.Escape(options[i]));
        }

        sb.Append(')');
        return close;
    }

    private static string[] Split(string input)
        => input.Split('/', StringSplitOptions.None)[1..];
}
