namespace HaPlay.ViewModels.Dialogs;

internal static class OutputNameUniqueness
{
    public static HashSet<string> CreateNameSet(IEnumerable<string> names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        return set;
    }

    public static string MakeUniqueDefaultName(string baseName, IReadOnlySet<string> existingNames)
    {
        var trimmedBase = string.IsNullOrWhiteSpace(baseName) ? "Output" : baseName.Trim();
        if (!existingNames.Contains(trimmedBase))
            return trimmedBase;

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{trimmedBase} {i}";
            if (!existingNames.Contains(candidate))
                return candidate;
        }

        return $"{trimmedBase} {Guid.NewGuid():N}";
    }

    public static bool TryFindDuplicate(
        string displayName,
        IReadOnlyCollection<string> existingNames,
        out string duplicateName)
    {
        var trimmed = displayName.Trim();
        foreach (var existing in existingNames)
        {
            if (string.Equals(trimmed, existing, StringComparison.OrdinalIgnoreCase))
            {
                duplicateName = existing;
                return true;
            }
        }

        duplicateName = string.Empty;
        return false;
    }
}
