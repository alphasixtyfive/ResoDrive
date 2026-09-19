using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

public sealed record HostRequest(
    string Command,
    Guid? MountId = null,
    Guid? SyncJobId = null,
    bool Confirmed = false,
    string? ExpectedHostBaseDirectory = null);

public sealed record HostMountStatus(
    Guid MountId,
    string Lifecycle,
    string Status);

public sealed record HostSyncStatus(
    Guid MountId,
    Guid SyncJobId,
    string Lifecycle,
    string Status,
    DateTimeOffset? CompletedAt,
    long? BytesTransferred = null,
    long? TotalBytes = null,
    double? ProgressPercent = null,
    long? ChecksCompleted = null,
    long? TotalChecks = null,
    long? TransfersCompleted = null,
    long? TotalTransfers = null,
    long? Errors = null,
    double? SpeedBytesPerSecond = null,
    double? EtaSeconds = null,
    double? ElapsedSeconds = null);

public sealed record HostResponse(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<HostMountStatus>? Mounts = null,
    IReadOnlyList<HostSyncStatus>? SyncJobs = null,
    string? HostBaseDirectory = null,
    int? HostProcessId = null);

public static class HostProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string GetPipeName(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root)).ToUpperInvariant();
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}\n{root}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"rdrive.host.{hash}";
    }

    public static bool IsSameBaseDirectory(string? left, string? right) =>
        string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            StringComparison.OrdinalIgnoreCase
        );

    public static bool AcceptsBaseDirectory(string? expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected) && IsSameBaseDirectory(expected, actual);

    public static HostMountStatus ToStatus(MountSnapshot snapshot) => new(
        snapshot.MountId.Value,
        snapshot.Lifecycle.ToString(),
        snapshot.StatusText ?? snapshot.Lifecycle.ToString());

    public static HostSyncStatus ToStatus(SyncSnapshot snapshot) => new(
        snapshot.MountId.Value,
        snapshot.JobId.Value,
        snapshot.Lifecycle.ToString(),
        snapshot.StatusText ?? snapshot.Lifecycle.ToString(),
        snapshot.CompletedAt,
        snapshot.BytesTransferred,
        snapshot.TotalBytes,
        snapshot.ProgressPercent,
        snapshot.ChecksCompleted,
        snapshot.TotalChecks,
        snapshot.TransfersCompleted,
        snapshot.TotalTransfers,
        snapshot.Errors,
        snapshot.SpeedBytesPerSecond,
        snapshot.EtaSeconds,
        snapshot.ElapsedSeconds);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        var length = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is <= 0 or > 1024 * 1024)
        {
            throw new InvalidDataException("The host message length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions);
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public static class HostClient
{
    public static Task<HostResponse> SendToInstallationAsync(
        HostRequest request, string installationDirectory, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        if (!Path.IsPathFullyQualified(installationDirectory))
            throw new ArgumentException("An absolute installation directory is required.", nameof(installationDirectory));
        return SendCoreAsync(request with { ExpectedHostBaseDirectory = Path.GetFullPath(installationDirectory) },
            timeout, enforceInstallation: false, cancellationToken);
    }

    public static Task<HostResponse> SendAsync(
        HostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var responseTimeout = request.Command.Equals("reload", StringComparison.OrdinalIgnoreCase) ||
            request.Command.Equals("activate-runtime", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(45)
            : request.Command.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
              request.Command.Equals("check-uploads", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(8);
        return SendCoreAsync(request, responseTimeout, enforceInstallation: true, cancellationToken);
    }

    public static async Task<HostResponse> SendAsync(
        HostRequest request,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        return await SendCoreAsync(
            request,
            responseTimeout,
            enforceInstallation: true,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public static Task<HostResponse> ShutdownForeignHostAsync(
        string expectedHostBaseDirectory,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHostBaseDirectory);
        return SendCoreAsync(
            new HostRequest(
                "shutdown",
                Confirmed: confirmed,
                ExpectedHostBaseDirectory: expectedHostBaseDirectory
            ),
            TimeSpan.FromSeconds(8),
            enforceInstallation: false,
            cancellationToken
        );
    }

    private static async Task<HostResponse> SendCoreAsync(
        HostRequest request,
        TimeSpan responseTimeout,
        bool enforceInstallation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(responseTimeout, TimeSpan.Zero);
        var effectiveRequest = enforceInstallation
            ? request with { ExpectedHostBaseDirectory = AppContext.BaseDirectory }
            : request;
        var paths = new ApplicationPaths();
        using var pipe = CurrentUserPipe.CreateClient(HostProtocol.GetPipeName(paths));
        using var timeout = new CancellationTokenSource(responseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var connected = false;
        try
        {
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            connected = true;
            CurrentUserPipe.ValidateServerIdentity(pipe);
            var serverProcessId = CurrentUserPipe.GetServerProcessId(pipe);
            await HostProtocol.WriteAsync(pipe, effectiveRequest, linked.Token).ConfigureAwait(false);
            var response = await HostProtocol.ReadAsync<HostResponse>(pipe, linked.Token).ConfigureAwait(false)
                ?? new HostResponse(false, "host.invalid_response", "The background host returned an empty response.");
            if (enforceInstallation &&
                response.Succeeded &&
                !HostProtocol.IsSameBaseDirectory(response.HostBaseDirectory, AppContext.BaseDirectory))
            {
                return new HostResponse(
                    false,
                    "host.different_installation",
                    "Another ResoDrive installation is already managing this account.",
                    response.Mounts,
                    response.SyncJobs,
                    response.HostBaseDirectory
                );
            }
            return response with { HostProcessId = serverProcessId };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return connected
                ? new HostResponse(false, "host.response_timeout", "The ResoDrive background host did not finish the request in time.")
                : new HostResponse(false, "host.unavailable", "The ResoDrive background host is not available.");
        }
        catch (IOException exception)
        {
            return new HostResponse(
                false,
                connected ? "host.connection_lost" : "host.unavailable",
                exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new HostResponse(false, "host.access_denied", exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            return new HostResponse(false, "host.invalid_response", exception.Message);
        }
    }

}
