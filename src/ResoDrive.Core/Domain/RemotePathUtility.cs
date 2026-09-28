namespace ResoDrive.Core.Domain;

public static class RemotePathUtility
{
    public const int MaximumLength = 2_048;

    public static string Normalize(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (normalized == "/")
        {
            return string.Empty;
        }

        return normalized.EndsWith('/') &&
               !normalized.EndsWith("//", StringComparison.Ordinal)
            ? normalized[..^1]
            : normalized;
    }

    public static bool IsWellFormed(string? path)
    {
        if (path is null || path.Length > MaximumLength || path.Contains('\\') ||
            path.Contains("//", StringComparison.Ordinal) || path.Any(IsUnsafeCharacter))
        {
            return false;
        }

        return !path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    public static string FormatSource(string remoteName, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        return $"{remoteName.Trim().TrimEnd(':')}:{Normalize(path)}";
    }

    public static string Display(string name, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = Normalize(path);
        return normalized.Length == 0
            ? name
            : $"{name} · {normalized}";
    }

    private static bool IsUnsafeCharacter(char value) =>
        value == '\0' || value == '\r' || value == '\n' || char.IsControl(value);
}
