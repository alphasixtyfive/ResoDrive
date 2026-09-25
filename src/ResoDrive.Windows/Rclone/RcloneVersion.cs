using System.Text.RegularExpressions;

namespace ResoDrive.Windows;

/// <summary>Versions that can be reported safely in a storage User-Agent.</summary>
internal static partial class RcloneVersion
{
    public static bool IsReportable(string? version) =>
        version is { Length: <= 32 } && Pattern().IsMatch(version);

    [GeneratedRegex(@"\Av[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
