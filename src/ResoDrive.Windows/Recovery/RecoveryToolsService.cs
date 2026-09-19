using System.Text.Json;
using System.Text.RegularExpressions;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed partial class RecoveryToolsService
{
    private readonly ApplicationPaths _paths;

    public RecoveryToolsService(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<OperationResult<string>> ExportSettingsAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var destination = ValidateDestination(destinationPath, ".json");
            if (!File.Exists(_paths.SettingsFile))
                return Result.Failure<string>("settings.export_missing", "No settings file is available to export.");
            if (IsWithinDirectory(destination, _paths.Root))
                return Result.Failure<string>("settings.export_same_file", "Choose a location outside the ResoDrive data folder.");

            var contents = await File.ReadAllBytesAsync(_paths.SettingsFile, cancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument.Parse(contents))
            {
            }
            await WriteAtomicallyAsync(destination, contents, cancellationToken).ConfigureAwait(false);
            return Result.Success(destination);
        }
        catch (JsonException exception)
        {
            return Result.Failure<string>("settings.export_invalid", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result.Failure<string>("settings.export_failed", exception.Message);
        }
    }

    public async Task ExportDiagnosticReportAsync(string destinationPath, string report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var destination = ValidateDestination(destinationPath, ".txt");
        if (IsWithinDirectory(destination, _paths.Root))
            throw new ArgumentException("Choose a location outside the ResoDrive data folder.", nameof(destinationPath));
        await WriteAtomicallyAsync(destination, System.Text.Encoding.UTF8.GetBytes(report), cancellationToken).ConfigureAwait(false);
    }

    public static string Sanitize(string value)
    {
        var sanitized = SecretPattern().Replace(value, "$1$2<redacted>");
        sanitized = FlagSecretPattern().Replace(sanitized, "$1 <redacted>");
        sanitized = AuthorizationPattern().Replace(sanitized, "$1 <redacted>");
        sanitized = UriCredentialsPattern().Replace(sanitized, "$1<redacted>@");
        sanitized = UriHostPattern().Replace(sanitized, "$1://$2<host>");
        sanitized = UriPathPattern().Replace(sanitized, "$1/<path>");
        sanitized = EndpointValuePattern().Replace(sanitized, "$1$2<host>");
        sanitized = NaturalHostPattern().Replace(sanitized, "$1 <host>");
        sanitized = Ipv6Pattern().Replace(sanitized, "<host>");
        sanitized = Ipv4Pattern().Replace(sanitized, "<host>");
        sanitized = HostNamePattern().Replace(sanitized, "<host>");
        sanitized = UserDirectoryPattern().Replace(sanitized, "<user>");
        sanitized = UncPathPattern().Replace(sanitized, "<path>");
        sanitized = UnixPathPattern().Replace(sanitized, "<path>");
        sanitized = WindowsPathPattern().Replace(sanitized, "<path>");
        return sanitized;
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ValidateDestination(string destinationPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        var destination = Path.GetFullPath(destinationPath);
        if (!Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The destination must be a {extension} file.", nameof(destinationPath));
        return destination;
    }

    private static bool IsWithinDirectory(string candidatePath, string directoryPath)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath)),
            Path.GetFullPath(candidatePath));
        return relative.Equals(".", StringComparison.Ordinal) ||
            (!Path.IsPathFullyQualified(relative) &&
             !relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [GeneratedRegex("(?i)([\"']?\\b(?:password|passwd|pass|token|secret|api[-_]?key)\\b[\"']?)(\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)(--?(?:password|passwd|pass|token|secret|api[-_]?key))(?:=|\s+)\S+")]
    private static partial Regex FlagSecretPattern();

    [GeneratedRegex(@"(?i)\b(authorization|bearer)\s+[^\s,;]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)(https?://)[^/@\s]+@")]
    private static partial Regex UriCredentialsPattern();

    [GeneratedRegex(@"(?i)\b(https?|sftp|webdav)://(<redacted>@)?[^/\s,;\""']+")]
    private static partial Regex UriHostPattern();

    [GeneratedRegex(@"(?i)(://(?:<redacted>@)?<host>)/[^\s,;\""']+")]
    private static partial Regex UriPathPattern();

    [GeneratedRegex("(?i)([\"']?\\b(?:server|host|hostname|address|endpoint)\\b[\"']?)(\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex EndpointValuePattern();

    [GeneratedRegex(@"(?i)\b(lookup|resolve(?:d|s|ing)?|connect(?:ed|s|ing)?(?:\s+to)?|dial(?:ed|s|ing)?)\s+[a-z0-9][a-z0-9-]{1,62}\b")]
    private static partial Regex NaturalHostPattern();

    [GeneratedRegex(@"(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![\w:])")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?i)\b(?:[a-z0-9-]+\.)+[a-z]{2,63}\b")]
    private static partial Regex HostNamePattern();

    [GeneratedRegex(@"(?i)[a-z]:\\users\\[^\\\s]+(?:\\[^\s,;]+)*")]
    private static partial Regex UserDirectoryPattern();

    [GeneratedRegex(@"\\\\[^\\\s]+\\[^,;\""\r\n]+")]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(@"(?i)(?<![:/\w])/(?:[^/\s,;\""']+/)*[^/\s,;\""']+(?:\s+[^,;\""\r\n]+)?")]
    private static partial Regex UnixPathPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:[\\/][^,;\""\r\n]+")]
    private static partial Regex WindowsPathPattern();
}
