using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public sealed record RemoteWipeRegistration(
    Guid MountId,
    string ProbeEndpoint,
    string ServerBaseUrl,
    string Username,
    string AppToken)
{
    public override string ToString() => $"Remote-wipe registration for {MountId}";
}

/// <summary>
/// Stores the dedicated Nextcloud login-flow/app token needed by the remote-wipe
/// protocol. The file is DPAPI protected and is never included in diagnostics.
/// </summary>
public sealed class RemoteWipeStore
{
    private const int MaximumRegistrationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly ApplicationPaths _paths;
    private readonly DpapiSecretStore _protection;

    public RemoteWipeStore(ApplicationPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _protection = new DpapiSecretStore(paths);
    }

    public async Task<IReadOnlyList<RemoteWipeRegistration>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.RemoteWipeFile))
            return [];

        var json = await _protection.LoadProtectedFileAsync(
            _paths.RemoteWipeFile, MaximumRegistrationBytes, cancellationToken).ConfigureAwait(false);
        var registrations = JsonSerializer.Deserialize<List<RemoteWipeRegistration>>(json, JsonOptions);
        if (registrations is null)
            throw new CryptographicException("The protected remote-wipe file is invalid.");
        return registrations;
    }

    public async Task<string> CreateStagedAsync(
        IEnumerable<RemoteWipeRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var normalized = registrations
            .Where(registration => registration.MountId != Guid.Empty)
            .GroupBy(registration => registration.MountId)
            .Select(group => group.Last())
            .ToArray();
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumRegistrationBytes)
            throw new CryptographicException("The remote-wipe registration catalog is too large.");
        var stagedPath = _paths.RemoteWipeFile + $".{Guid.NewGuid():N}.setup-wipe";
        try
        {
            await _protection.SaveProtectedFileAsync(json, stagedPath, cancellationToken)
                .ConfigureAwait(false);
            return stagedPath;
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
