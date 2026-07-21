using System.Globalization;

namespace S.Media.Tools.NDIVisualizer;

/// <summary>Small blocking console-prompt helpers for the setup wizard (numbered pick lists + typed reads).</summary>
internal static class ConsolePrompt
{
    /// <summary>
    /// Prints a numbered menu and reads a selection. <paramref name="defaultIndex"/> (when in range) is used
    /// on an empty line. Returns the chosen item's index.
    /// </summary>
    public static int SelectIndex<T>(string title, IReadOnlyList<T> items, Func<T, string> label, int defaultIndex = -1)
    {
        if (items.Count == 0)
            throw new InvalidOperationException($"{title}: nothing to choose from.");

        Console.WriteLine();
        Console.WriteLine(title);
        for (var i = 0; i < items.Count; i++)
        {
            var marker = i == defaultIndex ? " (default)" : "";
            Console.WriteLine($"  [{i + 1}] {label(items[i])}{marker}");
        }

        while (true)
        {
            var hasDefault = defaultIndex >= 0 && defaultIndex < items.Count;
            Console.Write(hasDefault ? $"Select 1-{items.Count} [{defaultIndex + 1}]: " : $"Select 1-{items.Count}: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line) && hasDefault)
                return defaultIndex;
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= items.Count)
                return n - 1;
            Console.WriteLine("  Please enter a number from the list.");
        }
    }

    /// <summary>Reads a line of text; an empty line returns <paramref name="defaultValue"/>.</summary>
    public static string ReadString(string prompt, string defaultValue)
    {
        Console.Write(string.IsNullOrEmpty(defaultValue) ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? defaultValue : line.Trim();
    }

    /// <summary>Reads a positive integer; an empty line returns <paramref name="defaultValue"/>.</summary>
    public static int ReadInt(string prompt, int defaultValue, int min = 1, int max = int.MaxValue)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line))
                return defaultValue;
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max)
                return n;
            Console.WriteLine($"  Please enter a whole number between {min} and {max}.");
        }
    }

    /// <summary>
    /// Reads a resolution as <c>WIDTHxHEIGHT</c> (also accepts a space/comma separator). Even dimensions are
    /// enforced (projectM/NDI want even sizes) by rounding down.
    /// </summary>
    public static (int Width, int Height) ReadResolution(string prompt, int defaultW, int defaultH)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultW}x{defaultH}]: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line))
                return (defaultW, defaultH);

            var parts = line.Split(['x', 'X', ' ', ',', '*'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && w is >= 16 and <= 7680 && h is >= 16 and <= 4320)
                return (w & ~1, h & ~1);

            Console.WriteLine("  Please enter a resolution like 1920x1080 (16-7680 by 16-4320).");
        }
    }

    /// <summary>
    /// Reads a comma/space separated list of channel numbers in <c>1..max</c>. An empty line selects all.
    /// Returns distinct numbers in the order typed.
    /// </summary>
    public static int[] ReadChannels(string prompt, int max, IReadOnlyList<int> defaultChannels)
    {
        var def = defaultChannels.Where(c => c >= 1 && c <= max).Distinct().ToArray();
        if (def.Length == 0)
            def = Enumerable.Range(1, max).ToArray();
        var defText = string.Join(" ", def);

        while (true)
        {
            Console.Write($"{prompt} (1-{max}, space/comma separated, blank = all) [{defText}]: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line))
                return def;

            var tokens = line.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var picked = new List<int>();
            var ok = true;
            foreach (var t in tokens)
            {
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 1 && c <= max)
                {
                    if (!picked.Contains(c))
                        picked.Add(c);
                }
                else
                {
                    Console.WriteLine($"  '{t}' is not a channel between 1 and {max}.");
                    ok = false;
                    break;
                }
            }

            if (ok && picked.Count > 0)
                return picked.ToArray();
            if (ok)
                Console.WriteLine("  Please select at least one channel.");
        }
    }

    /// <summary>Yes/no prompt. <paramref name="defaultYes"/> picks the answer for an empty line.</summary>
    public static bool Confirm(string prompt, bool defaultYes)
    {
        var suffix = defaultYes ? "[Y/n]" : "[y/N]";
        while (true)
        {
            Console.Write($"{prompt} {suffix}: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line))
                return defaultYes;
            switch (line[0])
            {
                case 'y' or 'Y':
                    return true;
                case 'n' or 'N':
                    return false;
                default:
                    Console.WriteLine("  Please answer y or n.");
                    break;
            }
        }
    }
}
