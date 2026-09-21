using System.Security.Cryptography;
using System.Text.Json;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

public sealed record RemoteWipeEnrollmentResult(IReadOnlyDictionary<Guid, string> Statuses, bool RegistrationsChanged = false);

/// <summary>Recovers the local wipe-check list from existing Nextcloud app-password connections.</summary>
public sealed class RemoteWipeEnrollmentService
{
    internal const string Configured = "Remote wipe configured";
    internal const string Attention = "Remote wipe setup needs attention";
    internal const string NotConfigured = "Remote wipe not configured";
    private readonly ApplicationPaths _paths;
    private readonly AccountDataGuard _accountData;
    private readonly RemoteWipeStore _registrations;
    private readonly IRcloneRemoteConfigurationAccess _configuration;
    private readonly string _executable;
    private string? _lastSignature;
    private IReadOnlyDictionary<Guid, string> _lastStatuses = new Dictionary<Guid, string>();

    public RemoteWipeEnrollmentService(ApplicationPaths paths)
        : this(paths, new RcloneRemoteConfigurationAccess(), new RcloneRuntimeLocator(paths).ExecutablePath) { }

    internal RemoteWipeEnrollmentService(ApplicationPaths paths, IRcloneRemoteConfigurationAccess configuration, string executable)
    {
        _paths = paths;
        _accountData = new(paths);
        _registrations = new(paths);
        _configuration = configuration;
        _executable = executable;
    }

    public async Task<RemoteWipeEnrollmentResult> EnrollAsync(
        IReadOnlyList<MountDefinition> definitions, CancellationToken cancellationToken = default)
    {
        var statuses = definitions.Where(IsWebDav).ToDictionary(definition => definition.Id.Value, _ => NotConfigured);
        var published = false;
        if (definitions.Count == 0) return new(statuses);
        try
        {
            // Setup and wipe share this lock. Credentials stay in memory; rclone.conf is never changed.
            using var lease = await _accountData.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var registered = await _registrations.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in definitions.Where(definition => registered.Any(r => r.MountId == definition.Id.Value)))
                statuses[definition.Id.Value] = Configured;
            if (!File.Exists(_paths.ConfigFile) || !File.Exists(_paths.ConfigSecretFile) || !File.Exists(_executable))
                return new(statuses);
            var signature = await SignatureAsync(definitions, cancellationToken).ConfigureAwait(false);
            if (signature == _lastSignature) return new(_lastStatuses);

            // A UI operation may have saved new settings while the host was awaiting this lock.
            using var store = new AtomicSettingsStore(_paths);
            var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Succeeded || settings.Value is null) throw new IOException("The connection settings are unavailable.");
            var currentIds = settings.Value.Mounts
                .Where(mount => definitions.Any(definition => definition.Id.Value == mount.Id && definition.RemoteName == mount.RemoteName))
                .Select(mount => mount.Id).ToHashSet();
            var currentDefinitions = definitions.Where(definition => currentIds.Contains(definition.Id.Value)).ToArray();
            var remotes = await _configuration.ReadAsync(_executable, _paths.ConfigFile, RclonePasswordCommand.Create(),
                currentDefinitions.Select(d => d.RemoteName).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var updated = registered.ToDictionary(item => item.MountId);
            var changed = false;
            foreach (var definition in currentDefinitions)
            {
                if (!remotes.TryGetValue(definition.RemoteName, out var values) || !LooksLikeNextcloud(values)) continue;
                if (!TryCreateRegistration(definition.Id.Value, values, out var candidate))
                {
                    statuses[definition.Id.Value] = Attention;
                    continue;
                }
                if (!updated.TryGetValue(candidate.MountId, out var previous) || previous != candidate)
                {
                    updated[candidate.MountId] = candidate;
                    changed = true;
                }
                statuses[definition.Id.Value] = Configured;
            }
            if (changed)
            {
                var staged = await _registrations.CreateStagedAsync(updated.Values, cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(staged, _paths.RemoteWipeFile, overwrite: true);
                    published = true;
                }
                finally { DeleteStaged(staged); }
            }
            _lastSignature = await SignatureAsync(definitions, cancellationToken).ConfigureAwait(false);
            _lastStatuses = statuses;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Parser errors may contain source credentials. Report only a fixed status.
            foreach (var definition in definitions.Where(definition => IsWebDav(definition) ||
                         string.IsNullOrWhiteSpace(definition.ConnectionType) || statuses.ContainsKey(definition.Id.Value)))
                statuses[definition.Id.Value] = Attention;
        }
        return new(statuses, published);
    }

    private static bool IsWebDav(MountDefinition definition) =>
        string.Equals(definition.ConnectionType, "WebDAV", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(definition.ConnectionType, "Nextcloud", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeNextcloud(IReadOnlyDictionary<string, string> values) =>
        Get(values, "type").Equals("webdav", StringComparison.OrdinalIgnoreCase) &&
        (Get(values, "vendor").Equals("nextcloud", StringComparison.OrdinalIgnoreCase) ||
         ((string.IsNullOrEmpty(Get(values, "vendor")) || Get(values, "vendor").Equals("other", StringComparison.OrdinalIgnoreCase)) &&
          (Get(values, "url").Contains("/remote.php/dav/files/", StringComparison.OrdinalIgnoreCase) ||
           Get(values, "url").Contains("/remote.php/webdav", StringComparison.OrdinalIgnoreCase))));

    internal static bool TryCreateRegistration(Guid mountId, IReadOnlyDictionary<string, string> values,
        out RemoteWipeRegistration registration)
    {
        registration = null!;
        if (!LooksLikeNextcloud(values) || !string.IsNullOrEmpty(Get(values, "bearer_token")) ||
            !string.IsNullOrEmpty(Get(values, "bearer_token_command")) || !string.IsNullOrEmpty(Get(values, "headers")) ||
            !Uri.TryCreate(Get(values, "url"), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
        var path = endpoint.AbsolutePath;
        const string modernPath = "/remote.php/dav/files/";
        const string legacyPath = "/remote.php/webdav";
        var marker = path.IndexOf(modernPath, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            marker = path.IndexOf(legacyPath, StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || (path.Length > marker + legacyPath.Length && path[marker + legacyPath.Length] != '/')) return false;
        }
        else if (path.Length == marker + modernPath.Length) return false;
        var prefix = path[..marker];
        if (prefix.Contains('%') || prefix.Contains('\\')) return false;
        var username = Get(values, "user");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 1024 || username.Any(char.IsControl) || username.Contains(':'))
            return false;
        string password;
        try { password = RcloneObscuredSecret.Reveal(Get(values, "pass")); }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
        if (string.IsNullOrWhiteSpace(password)) return false;
        var serverBase = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + prefix + "/");
        registration = new(mountId, endpoint.AbsoluteUri, serverBase.AbsoluteUri, username, password);
        return true;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value : string.Empty;

    private async Task<string> SignatureAsync(IReadOnlyList<MountDefinition> definitions, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(definitions.Select(d => new { d.Id, d.RemoteName, d.ConnectionType })));
        foreach (var path in new[] { _paths.ConfigFile, _paths.RemoteWipeFile, _paths.SettingsFile })
        {
            if (!File.Exists(path)) { hash.AppendData([0]); continue; }
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("The saved connection data is too large.");
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            try { hash.AppendData(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException or
        CryptographicException or JsonException or FormatException or InvalidOperationException or HttpRequestException or
        TimeoutException or ArgumentException or System.ComponentModel.Win32Exception;

    private static void DeleteStaged(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
