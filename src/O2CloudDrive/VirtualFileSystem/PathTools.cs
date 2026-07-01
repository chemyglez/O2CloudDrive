namespace O2CloudDrive.VirtualFileSystem;

internal static class PathTools
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "\\")
        {
            return "\\";
        }

        var normalized = path.Replace('/', '\\').Trim();
        while (normalized.Contains("\\\\", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (!normalized.StartsWith('\\'))
        {
            normalized = "\\" + normalized;
        }

        normalized = normalized.TrimEnd('\\');
        return string.IsNullOrWhiteSpace(normalized) ? "\\" : normalized;
    }

    public static IReadOnlyList<string> GetSegments(string path)
    {
        var normalized = Normalize(path);
        return normalized == "\\"
            ? []
            : normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string GetParent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "\\")
        {
            return "\\";
        }

        var index = normalized.LastIndexOf('\\');
        return index <= 0 ? "\\" : normalized[..index];
    }

    public static string GetName(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "\\")
        {
            return string.Empty;
        }

        var index = normalized.LastIndexOf('\\');
        return normalized[(index + 1)..];
    }
}
