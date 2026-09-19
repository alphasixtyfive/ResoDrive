using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Setup;

namespace ResoDrive.Windows;

public sealed record ProfileProvisioningResult(
    MountSettings NewMount,
    string ConnectionSummary,
    bool StartWithWindows,
    SetupFileTransaction Files) : IDisposable
{
    public void Dispose() => Files.Dispose();
}

internal interface IProfileRcloneRunner
{
    Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken);
}

internal sealed class ProfileRcloneRunner : IProfileRcloneRunner
{
    public Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
            executablePath, arguments, timeout, standardInput, environment, cancellationToken);
}

public sealed class ProfileProvisioningService
{
    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private readonly ApplicationPaths _paths;
    private readonly RcloneRuntimeLocator _rclone;
    private readonly IConfigSecretStore _secretStore;
    private readonly ISetupProfileCatalog _profileCatalog;
    private readonly IRcloneConfigMutationService _mutation;
    private readonly IProfileRcloneRunner _runner;
    private readonly HttpClient _httpClient;
    private readonly RemoteWipeStore _remoteWipe;
    private readonly AccountDataGuard _accountData;

    public ProfileProvisioningService(
        ApplicationPaths paths,
        ISetupProfileCatalog profileCatalog,
        RcloneRuntimeLocator? rclone = null,
        DpapiSecretStore? secretStore = null)
        : this(
            paths,
            profileCatalog,
            rclone ?? new RcloneRuntimeLocator(paths),
            secretStore ?? new DpapiSecretStore(paths),
            new RcloneConfigMutationService(),
            new ProfileRcloneRunner(),
            SharedHttpClient)
    {
    }

    internal ProfileProvisioningService(
        ApplicationPaths paths,
        ISetupProfileCatalog profileCatalog,
        RcloneRuntimeLocator rclone,
        IConfigSecretStore secretStore,
        IRcloneConfigMutationService mutation,
        IProfileRcloneRunner runner,
        HttpClient httpClient)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _rclone = rclone ?? throw new ArgumentNullException(nameof(rclone));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _remoteWipe = new RemoteWipeStore(_paths);
        _accountData = new(_paths);
    }

    public async Task<OperationResult<ProfileProvisioningResult>> ProvisionAsync(
        ProfileSetupRequest request,
        string password,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = _profileCatalog.Find(request.ProfileId);
        if (profile is null)
            return Failure("setup.profile", "The selected setup profile is not available.");

        string normalizedPassword;
        string normalizedSftpKeyFile = string.Empty;
        Uri endpoint;
        try
        {
            SetupProfileValidator.ValidateUsername(request.Username);
            (normalizedPassword, endpoint) = profile.Connection switch
            {
                WebDavConnectionDefinition webDav => (
                    webDav.Vendor == WebDavVendor.Nextcloud
                        ? NormalizeAppPassword(password)
                        : NormalizeExactPassword(password),
                    SetupProfileValidator.CreateWebDavUri(profile, request.Username)),
                SftpConnectionDefinition { Authentication: SftpAuthenticationMethod.Password } sftp => (
                    NormalizeExactPassword(password),
                    CreateSftpEndpoint(sftp)),
                SftpConnectionDefinition { Authentication: SftpAuthenticationMethod.PrivateKey } sftp => (
                    NormalizeOptionalSecret(password),
                    CreateSftpEndpoint(sftp)),
                _ => throw new ArgumentException("The connection type is not supported.")
            };
            if (profile.Connection is SftpConnectionDefinition
                { Authentication: SftpAuthenticationMethod.PrivateKey })
            {
                normalizedSftpKeyFile = ValidateSftpKeyFile(request.SftpKeyFilePath);
            }
        }
        catch (ArgumentException exception)
        {
            return Failure("setup.input", exception.Message);
        }

        progress?.Report("Checking rclone");
        var installation = await _rclone.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!installation.Succeeded || installation.Value?.ExecutablePath is null)
            return Failure(
                installation.Error?.Code ?? "rclone.bundled_invalid",
                installation.Error?.Message ?? "The managed rclone component is not installed.",
                installation.Error?.IsTransient ?? false);

        if (profile.Connection is WebDavConnectionDefinition)
        {
            progress?.Report("Checking account");
            var credentialCheck = await CheckWebDavCredentialsAsync(
                endpoint, request.Username, normalizedPassword, cancellationToken).ConfigureAwait(false);
            if (!credentialCheck.Succeeded)
                return Failure(
                    credentialCheck.Error?.Code ?? "setup.credentials",
                    credentialCheck.Error?.Message ?? "The account could not be verified.",
                    credentialCheck.Error?.IsTransient ?? false);
        }

        progress?.Report("Preparing protected configuration");
        SetupFileTransaction? transaction = null;
        string? initialConfig = null;
        string? stagedSecret = null;
        string? stagedConfig = null;
        string? stagedRemoteWipe = null;
        string configPassword;
        string configPasswordCommand;
        try
        {
            using var accountLease = await _accountData.AcquireAsync(cancellationToken).ConfigureAwait(false);
            _paths.EnsureCreated();
            var configExists = File.Exists(_paths.ConfigFile);
            if (configExists)
            {
                if (!_secretStore.Exists)
                    return Failure("setup.config_secret_missing",
                        "The existing encrypted rclone configuration cannot be changed because its Windows-protected password is missing.");
                try
                {
                    configPassword = await _secretStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                    configPasswordCommand = RclonePasswordCommand.Create();
                }
                catch (Exception exception) when (exception is FormatException or CryptographicException or InvalidOperationException or IOException)
                {
                    return Failure("setup.config_secret_unreadable",
                        "The existing encrypted rclone configuration cannot be changed because its Windows-protected password is unreadable. The existing files were preserved.");
                }
                var encryptionCheck = await RunCheckedAsync(
                    installation.Value.ExecutablePath,
                    ["--config", _paths.ConfigFile, "--ask-password=false", "--password-command", configPasswordCommand,
                        "config", "encryption", "check"],
                    null, null, cancellationToken).ConfigureAwait(false);
                if (!encryptionCheck.Succeeded)
                    return FailureFrom(encryptionCheck, "setup.encryption_check",
                        "The existing encrypted rclone configuration could not be verified.");
                SensitiveFilePermissions.RestrictToCurrentUser(_paths.ConfigFile);
            }
            else
            {
                configPassword = DpapiSecretStore.CreateRandomPassword();
                stagedSecret = CreateSecretStagingPath(_paths.ConfigSecretFile);
                await _secretStore.SaveProtectedFileAsync(
                    configPassword, stagedSecret, cancellationToken).ConfigureAwait(false);
                configPasswordCommand = RclonePasswordCommand.Create(stagedSecret);
                initialConfig = CreateStagingPath(_paths.ConfigFile);
                await File.WriteAllTextAsync(initialConfig, string.Empty, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                var encryption = await RunCheckedAsync(
                    installation.Value.ExecutablePath,
                    ["--config", initialConfig, "--ask-password=false", "--password-command", configPasswordCommand,
                        "config", "encryption", "set"],
                    null, null, cancellationToken)
                    .ConfigureAwait(false);
                if (!encryption.Succeeded)
                    return FailureFrom(encryption, "setup.encryption", "The new rclone configuration could not be encrypted.");
            }

            var sourceConfig = configExists ? _paths.ConfigFile : initialConfig!;
            var remotes = await ListRemoteNamesAsync(
                installation.Value.ExecutablePath, sourceConfig, configPasswordCommand, cancellationToken).ConfigureAwait(false);
            if (!remotes.Succeeded || remotes.Value is null)
                return FailureFrom(remotes, "setup.remote_list", "The configured remote names could not be read.");
            var remoteName = AllocateRemoteName(profile.RemoteName, remotes.Value);
            var mount = ProfileSetupPlan.CreateMount(request, _profileCatalog, remoteName);
            if (!mount.Succeeded || mount.Value is null)
                return Failure(
                    mount.Error?.Code ?? "setup.mount",
                    mount.Error?.Message ?? "The drive settings are invalid.");

            OperationResult<RcloneConfigMutationResult> mutation = profile.Connection switch
            {
                WebDavConnectionDefinition webDav => await _mutation.AppendWebDavRemoteAsync(
                    installation.Value.ExecutablePath, sourceConfig, configPassword, configPasswordCommand,
                    new RcloneWebDavRemoteCreateRequest(
                        remoteName, endpoint, SetupProfileValidator.ToRcloneVendor(webDav.Vendor), request.Username, normalizedPassword),
                    cancellationToken).ConfigureAwait(false),
                SftpConnectionDefinition { Authentication: SftpAuthenticationMethod.Password } sftp =>
                    await _mutation.AppendSftpPasswordRemoteAsync(
                        installation.Value.ExecutablePath, sourceConfig, configPassword, configPasswordCommand,
                        new RcloneSftpPasswordRemoteCreateRequest(
                            remoteName, sftp, request.Username, normalizedPassword),
                        cancellationToken).ConfigureAwait(false),
                SftpConnectionDefinition { Authentication: SftpAuthenticationMethod.PrivateKey } sftp =>
                    await _mutation.AppendSftpKeyFileRemoteAsync(
                        installation.Value.ExecutablePath, sourceConfig, configPassword, configPasswordCommand,
                        new RcloneSftpKeyFileRemoteCreateRequest(
                            remoteName,
                            sftp,
                            request.Username,
                            normalizedSftpKeyFile,
                            normalizedPassword),
                        cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported connection type.")
            };
            if (!mutation.Succeeded || mutation.Value is null)
                return FailureFrom(mutation, "setup.config", "The remote could not be added to the rclone configuration.");
            stagedConfig = mutation.Value.StagedConfigPath;
            SensitiveFilePermissions.RestrictToCurrentUser(stagedConfig);

            progress?.Report("Checking remote");
            var source = RemotePathUtility.FormatSource(remoteName, mount.Value.RemotePath);
            var verification = await RunCheckedAsync(
                installation.Value.ExecutablePath,
                ["--config", stagedConfig, "--ask-password=false", "--password-command", configPasswordCommand,
                    "lsf", source, "--max-depth", "1"],
                null, null, cancellationToken).ConfigureAwait(false);
            if (!verification.Succeeded)
                return FailureFrom(verification, "setup.remote_check", "The configured remote could not be verified.");

            if (profile.Connection is WebDavConnectionDefinition
                { Vendor: WebDavVendor.Nextcloud } nextcloud)
            {
                var registrations = await _remoteWipe.LoadAsync(cancellationToken).ConfigureAwait(false);
                var registration = new RemoteWipeRegistration(
                    mount.Value.Id,
                    endpoint.ToString(),
                    nextcloud.BaseUrl.ToString(),
                    request.Username,
                    normalizedPassword);
                stagedRemoteWipe = await _remoteWipe.CreateStagedAsync(
                    registrations.Append(registration), cancellationToken).ConfigureAwait(false);
            }

            var files = new List<(string StagedPath, string DestinationPath)>();
            if (stagedSecret is not null)
            {
                files.Add((stagedSecret, _paths.ConfigSecretFile));
                stagedSecret = null;
            }
            files.Add((stagedConfig, _paths.ConfigFile));
            stagedConfig = null;
            if (stagedRemoteWipe is not null)
            {
                files.Add((stagedRemoteWipe, _paths.RemoteWipeFile));
                stagedRemoteWipe = null;
            }
            transaction = new SetupFileTransaction(files, _accountData);
            progress?.Report("Ready");
            return Result.Success(new ProfileProvisioningResult(
                mount.Value,
                endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}",
                request.StartWithWindows,
                transaction));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           CryptographicException or InvalidOperationException)
        {
            return Failure("setup.config", exception.Message);
        }
        finally
        {
            TryDelete(initialConfig);
            TryDelete(stagedSecret);
            TryDelete(stagedConfig);
            TryDelete(stagedRemoteWipe);
        }
    }

    internal static string AllocateRemoteName(string preferredName, IEnumerable<string> existingNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredName);
        ArgumentNullException.ThrowIfNull(existingNames);
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(preferredName)) return preferredName;
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var suffixText = $" {suffix}";
            var prefixLength = Math.Min(preferredName.Length, 128 - suffixText.Length);
            var candidate = preferredName[..prefixLength].TrimEnd() + suffixText;
            if (!existing.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("A unique rclone remote name could not be allocated.");
    }

    public static string NormalizeAppPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length > 2_048 || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("The app password is not valid.", nameof(password));
        var normalized = password.Trim();
        foreach (var dash in new[] { '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', '\u2212' })
            normalized = normalized.Replace(dash, '-');
        normalized = string.Concat(normalized.Where(character => !char.IsWhiteSpace(character)));
        return normalized.Length == 0
            ? throw new ArgumentException("An app password is required.", nameof(password))
            : normalized;
    }

    public static string NormalizeExactPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (password.Length > 2_048 || password.Any(char.IsControl))
            throw new ArgumentException("The password is not valid.", nameof(password));
        return password;
    }

    public static string NormalizeOptionalSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length > 2_048 || secret.Any(character => character is '\r' or '\n' or '\0'))
            throw new ArgumentException("The private key passphrase is not valid.", nameof(secret));
        return secret;
    }

    internal static string ValidateSftpKeyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1_024 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose a valid private key file.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.Length is <= 0 or > 1_048_576)
            throw new ArgumentException("The private key must be a readable file no larger than 1 MB.", nameof(path));
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fullPath;
    }

    private async Task<OperationResult> CheckWebDavCredentialsAsync(
        Uri endpoint, string username, string password, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), endpoint);
        request.Headers.Add("Depth", "0");
        request.Headers.UserAgent.ParseAdd("ResoDrive-setup/2.0");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or (HttpStatusCode)207) return Result.Success();
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? Result.Failure("setup.credentials_rejected", "The username or app password was not accepted.")
                : Result.Failure("setup.service_unavailable", $"The service returned HTTP {(int)response.StatusCode}.", true);
        }
        catch (HttpRequestException)
        {
            return Result.Failure("setup.service_unavailable", "The service could not be reached.", true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure("setup.service_timeout", "The service did not respond in time.", true);
        }
    }

    private async Task<OperationResult<IReadOnlyList<string>>> ListRemoteNamesAsync(
        string executablePath, string configPath, string configPasswordCommand, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync(
            executablePath,
            ["listremotes", "--config", configPath, "--ask-password=false", "--password-command", configPasswordCommand],
            null, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
            return Result.Failure<IReadOnlyList<string>>(
                result.Error?.Code ?? "setup.remote_list",
                result.Error?.Message ?? "The remote list could not be read.",
                result.Error?.IsTransient ?? false);
        var names = result.Value.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.EndsWith(':'))
            .Select(line => line[..^1])
            .Where(name => name.Length > 0)
            .ToArray();
        return Result.Success<IReadOnlyList<string>>(names);
    }

    private async Task<OperationResult<string>> RunCheckedAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            executablePath, arguments, TimeSpan.FromSeconds(45), environment, standardInput, cancellationToken)
            .ConfigureAwait(false);
        if (result.TimedOut) return Result.Failure<string>("rclone.timeout", "rclone did not respond in time.", true);
        if (result.ExitCode != 0)
            return Result.Failure<string>("rclone.failed", SafeError(result.StandardError), true);
        return Result.Success(result.StandardOutput);
    }

    private static Uri CreateSftpEndpoint(SftpConnectionDefinition connection)
    {
        var host = connection.Host.Contains(':', StringComparison.Ordinal) ? $"[{connection.Host}]" : connection.Host;
        return new Uri($"sftp://{host}:{connection.Port}/", UriKind.Absolute);
    }
    private static string SafeError(string error) => RcloneErrorMessage.Clean(error);
    private static string CreateStagingPath(string destination) => destination + $".{Guid.NewGuid():N}.setup-stage";
    private static string CreateSecretStagingPath(string destination) =>
        destination + $".{Guid.NewGuid():N}.setup-secret";
    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    private static OperationResult<ProfileProvisioningResult> Failure(string code, string message, bool transient = false) =>
        Result.Failure<ProfileProvisioningResult>(code, message, transient);
    private static OperationResult<ProfileProvisioningResult> FailureFrom(
        OperationResult result, string fallbackCode, string fallbackMessage) =>
        Failure(result.Error?.Code ?? fallbackCode, result.Error?.Message ?? fallbackMessage, result.Error?.IsTransient ?? false);
}
