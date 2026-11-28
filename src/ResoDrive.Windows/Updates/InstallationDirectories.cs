namespace ResoDrive.Windows;

/// <summary>Published installation paths; legacy paths are used only for migration.</summary>
public static class InstallationDirectories
{
    public const string ExecutableName = "resodrive.exe";
    public static string Current => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ResoDrive");
    public static string Legacy => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rdrive");
    public static string Executable => Path.Combine(Current, ExecutableName);
    public static string LegacyExecutable => Path.Combine(Legacy, ExecutableName);

    public static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks each installation once, including MSI paths ending in a dot.</summary>
    public static IReadOnlyList<string> DistinctPaths(IEnumerable<string> directories) => directories
        .Select(directory =>
        {
            if (!Path.IsPathFullyQualified(directory))
                throw new ArgumentException("An absolute installation directory is required.", nameof(directories));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string PreparationDataRoot(string requestedRoot)
    {
        // Preparation must contact the old host before migration. Never migrate here:
        // MSI may be running as another administrator, or may subsequently roll back.
        if (SamePath(requestedRoot, ApplicationPaths.DefaultRoot) &&
            !Directory.Exists(ApplicationPaths.DefaultRoot) && Directory.Exists(ApplicationPaths.LegacyDefaultRoot))
            return ApplicationPaths.LegacyDefaultRoot;
        return requestedRoot;
    }
}
