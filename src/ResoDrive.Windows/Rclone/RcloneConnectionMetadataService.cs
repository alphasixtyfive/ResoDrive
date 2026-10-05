using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed record RcloneConnectionMetadata(string? Host, string? Type)
{
    public string? Address { get; init; }
}

public static class RcloneConnectionMetadataService
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(8);

    public static async Task<OperationResult<IReadOnlyDictionary<string, RcloneConnectionMetadata>>> ReadAsync(
        string rclonePath,
        ApplicationPaths paths,
        string? remoteName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rclonePath);
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(rclonePath) || !File.Exists(paths.ConfigFile))
        {
            return Result.Success<IReadOnlyDictionary<string, RcloneConnectionMetadata>>(
                new Dictionary<string, RcloneConnectionMetadata>(StringComparer.OrdinalIgnoreCase));
        }

        var arguments = new List<string>
        {
            "config",
            "show",
            "--config",
            paths.ConfigFile,
            "--ask-password=false",
        };
        if (File.Exists(paths.ConfigSecretFile))
        {
            arguments.Add("--password-command");
            arguments.Add(RclonePasswordCommand.Create());
        }
        if (remoteName is not null)
        {
            arguments.Add("--");
            arguments.Add(remoteName);
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                rclonePath,
                arguments,
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0)
            {
                return Result.Failure<IReadOnlyDictionary<string, RcloneConnectionMetadata>>(
                    "rclone.metadata_failed",
                    "Storage connection details could not be inspected.",
                    true);
            }

            // rclone's redacted output also hides hosts. Inspect privately and
            // return only the allowlisted display metadata; never log raw output.
            return Result.Success<IReadOnlyDictionary<string, RcloneConnectionMetadata>>(
                Parse(result.StandardOutput));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            return Result.Failure<IReadOnlyDictionary<string, RcloneConnectionMetadata>>(
                "rclone.metadata_failed",
                exception.Message,
                true);
        }
    }

    internal static IReadOnlyDictionary<string, RcloneConnectionMetadata> Parse(string text)
    {
        var connections = new Dictionary<string, RcloneConnectionMetadata>(StringComparer.OrdinalIgnoreCase);
        string? remote = null;
        string? host = null;
        string? type = null;
        Uri? endpoint = null;
        string? port = null;
        void SaveRemote()
        {
            if (!string.IsNullOrWhiteSpace(remote) &&
                (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(type)))
            {
                var address = ConnectionAddress(type, host, port, endpoint);
                connections[remote] = new RcloneConnectionMetadata(host, type) { Address = address };
            }
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length >= 3 && line[0] == '[' && line[^1] == ']')
            {
                SaveRemote();
                remote = line[1..^1].Trim();
                host = null;
                type = null;
                endpoint = null;
                port = null;
                continue;
            }
            if (remote is null)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                type = value.ToLowerInvariant() switch
                {
                    "webdav" => "WebDAV",
                    "sftp" => "SFTP",
                    _ => null,
                };
            }
            else if (key.Equals("url", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.IdnHost;
                endpoint = uri;
            }
            else if (key.Equals("host", StringComparison.OrdinalIgnoreCase) &&
                     Uri.CheckHostName(value) != UriHostNameType.Unknown)
            {
                host = value;
            }
            else if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
            {
                port = value;
            }

        }

        SaveRemote();
        return connections;
    }

    private static string AddressHost(string host) => host.Contains(':')
        ? $"[{host.Trim('[', ']')}]" : host;

    private static string? ConnectionAddress(string? type, string? host, string? port, Uri? endpoint)
    {
        if (type != "SFTP")
            return endpoint is not null ? $"{endpoint.Scheme}://{AddressHost(endpoint.IdnHost)}:{endpoint.Port}" : null;
        if (string.IsNullOrWhiteSpace(host)) return null;
        var effectivePort = 22;
        if (!string.IsNullOrWhiteSpace(port) &&
            (!int.TryParse(port, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out effectivePort) || effectivePort is <= 0 or > 65535))
            return null;
        return $"sftp://{AddressHost(host)}:{effectivePort}";
    }
}
