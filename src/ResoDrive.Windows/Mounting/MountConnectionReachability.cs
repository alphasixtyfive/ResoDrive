using System.Net.Sockets;

namespace ResoDrive.Windows;

internal sealed record MountConnectionEndpoint(string Host, int Port)
{
    internal static MountConnectionEndpoint? FromMetadata(RcloneConnectionMetadata? metadata)
    {
        if (metadata?.Type is not ("WebDAV" or "SFTP") || metadata.UsesCustomTransport ||
            !Uri.TryCreate(metadata.Address, UriKind.Absolute, out var address) ||
            address.Port is <= 0 or > 65535 || address.HostNameType == UriHostNameType.Unknown)
            return null;
        return new(address.IdnHost, address.Port);
    }
}

internal static class MountConnectionReachability
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static bool CanProbeDirectly(IReadOnlyList<string> arguments, IEnumerable<string>? environmentNames = null)
    {
        // The engine must decide reachability when its route or transport differs
        // from the direct TCP connection available to this lightweight probe.
        if (arguments.Any(ArgumentChangesTransport)) return false;
        var configuredEnvironment = environmentNames ?? ConfiguredEnvironmentNames();
        return !configuredEnvironment.Any(EnvironmentChangesTransport);
    }

    internal static bool UsesCustomTransport(IReadOnlyDictionary<string, string> values) => values.Any(pair =>
        (pair.Key.Contains("proxy", StringComparison.OrdinalIgnoreCase) || pair.Key == "ssh") &&
        !string.IsNullOrWhiteSpace(pair.Value));

    private static bool ArgumentChangesTransport(string argument) =>
        argument.Contains("proxy", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--bind", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--sftp-ssh", StringComparison.OrdinalIgnoreCase);

    private static bool EnvironmentChangesTransport(string name) =>
        name.Contains("PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RCLONE_CONFIG_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RCLONE_SFTP_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RCLONE_BIND", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ConfiguredEnvironmentNames() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value?.ToString()))
            .Select(pair => pair.Key.ToString()!);

    internal static async Task<bool> IsReachableAsync(MountConnectionEndpoint endpoint, CancellationToken token)
    {
        // Probe only the configured destination; neither public Internet connectivity
        // nor a running VPN manager establishes that a private server is reachable.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}
