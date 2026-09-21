namespace ResoDrive.Windows;

internal static class RcloneUserAgentArguments
{
    public static IReadOnlyList<string> Create(IEnumerable<string> arguments) =>
        Create(arguments, Environment.GetEnvironmentVariable("RCLONE_USER_AGENT"));

    internal static IReadOnlyList<string> Create(IEnumerable<string> arguments, string? inheritedUserAgent)
    {
        // Keep explicit rclone overrides. The manager's default is only for
        // requests that would otherwise use rclone's generic identification.
        if (!string.IsNullOrEmpty(inheritedUserAgent) || arguments.Any(argument =>
                argument.Equals("--user-agent", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--user-agent=", StringComparison.OrdinalIgnoreCase)))
            return [];

        return ["--user-agent", ClientUserAgent.Value];
    }
}
