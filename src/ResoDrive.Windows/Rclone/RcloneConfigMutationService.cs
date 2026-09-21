using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ResoDrive.Core.Results;
using ResoDrive.Core.Setup;

namespace ResoDrive.Windows;

public sealed record RcloneWebDavRemoteCreateRequest(
    string RemoteName,
    Uri Endpoint,
    string Vendor,
    string Username,
    string Password)
{
    public override string ToString() => $"WebDAV remote '{RemoteName}'";
}

public sealed record RcloneConfigMutationResult(string StagedConfigPath, string RemoteName);

public sealed record RcloneSftpPasswordRemoteCreateRequest(
    string RemoteName,
    SftpConnectionDefinition Connection,
    string Username,
    string Password)
{
    public override string ToString() => $"SFTP remote '{RemoteName}'";
}

public sealed record RcloneSftpKeyFileRemoteCreateRequest(
    string RemoteName,
    SftpConnectionDefinition Connection,
    string Username,
    string KeyFilePath,
    string KeyFilePassphrase)
{
    public override string ToString() => $"SFTP remote '{RemoteName}'";
}

/// <summary>Stages and mutates an existing encrypted config without writing to the live file.</summary>
internal interface IRcloneConfigMutationService
{
    Task<OperationResult<RcloneConfigMutationResult>> AppendWebDavRemoteAsync(
        string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
        RcloneWebDavRemoteCreateRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<RcloneConfigMutationResult>> AppendSftpPasswordRemoteAsync(
        string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
        RcloneSftpPasswordRemoteCreateRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<RcloneConfigMutationResult>> AppendSftpKeyFileRemoteAsync(
        string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
        RcloneSftpKeyFileRemoteCreateRequest request, CancellationToken cancellationToken = default);
}

public sealed class RcloneConfigMutationService : IRcloneConfigMutationService
{
    private static readonly Regex RemoteNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex VendorPattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> SftpHostKeyAlgorithms = new(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521"
    };
    private readonly IRcloneRcSessionFactory _sessions;

    public RcloneConfigMutationService() : this(new RcloneRcSessionFactory()) { }

    internal RcloneConfigMutationService(IRcloneRcSessionFactory sessions) =>
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public Task<OperationResult<RcloneConfigMutationResult>> AppendWebDavRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        RcloneWebDavRemoteCreateRequest request,
        CancellationToken cancellationToken = default) =>
        AppendWebDavRemoteAsync(
            rcloneExecutablePath,
            liveConfigPath,
            configPassword,
            RclonePasswordCommand.Create(),
            request,
            cancellationToken);

    public Task<OperationResult<RcloneConfigMutationResult>> AppendSftpPasswordRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        RcloneSftpPasswordRemoteCreateRequest request,
        CancellationToken cancellationToken = default) =>
        AppendSftpPasswordRemoteAsync(
            rcloneExecutablePath,
            liveConfigPath,
            configPassword,
            RclonePasswordCommand.Create(),
            request,
            cancellationToken);

    public Task<OperationResult<RcloneConfigMutationResult>> AppendSftpKeyFileRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        RcloneSftpKeyFileRemoteCreateRequest request,
        CancellationToken cancellationToken = default) =>
        AppendSftpKeyFileRemoteAsync(
            rcloneExecutablePath,
            liveConfigPath,
            configPassword,
            RclonePasswordCommand.Create(),
            request,
            cancellationToken);

    public async Task<OperationResult<RcloneConfigMutationResult>> AppendWebDavRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        string configPasswordCommand,
        RcloneWebDavRemoteCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(rcloneExecutablePath, liveConfigPath, configPassword, request);
        if (!validation.Succeeded)
            return Result.Failure<RcloneConfigMutationResult>(validation.Error!.Code, validation.Error.Message);

        return await AppendRemoteAsync(
            rcloneExecutablePath, liveConfigPath, configPasswordCommand, request.RemoteName,
            (session, token) => session.CreateWebDavRemoteAsync(request, token), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OperationResult<RcloneConfigMutationResult>> AppendSftpPasswordRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        string configPasswordCommand,
        RcloneSftpPasswordRemoteCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(rcloneExecutablePath, liveConfigPath, configPassword, request);
        if (!validation.Succeeded)
            return Result.Failure<RcloneConfigMutationResult>(validation.Error!.Code, validation.Error.Message);

        return await AppendRemoteAsync(
            rcloneExecutablePath, liveConfigPath, configPasswordCommand, request.RemoteName,
            (session, token) => session.CreateSftpPasswordRemoteAsync(request, token), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OperationResult<RcloneConfigMutationResult>> AppendSftpKeyFileRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPassword,
        string configPasswordCommand,
        RcloneSftpKeyFileRemoteCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(rcloneExecutablePath, liveConfigPath, configPassword, request);
        if (!validation.Succeeded)
            return Result.Failure<RcloneConfigMutationResult>(validation.Error!.Code, validation.Error.Message);

        return await AppendRemoteAsync(
            rcloneExecutablePath, liveConfigPath, configPasswordCommand, request.RemoteName,
            (session, token) => session.CreateSftpKeyFileRemoteAsync(request, token), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationResult<RcloneConfigMutationResult>> AppendRemoteAsync(
        string rcloneExecutablePath,
        string liveConfigPath,
        string configPasswordCommand,
        string remoteName,
        Func<IRcloneRcSession, CancellationToken, Task> createRemote,
        CancellationToken cancellationToken)
    {

        var livePath = Path.GetFullPath(liveConfigPath);
        var stagedPath = CreateStagingPath(livePath);
        var retainStage = false;
        try
        {
            File.Copy(livePath, stagedPath, overwrite: false);
            await using var session = await _sessions.StartAsync(
                Path.GetFullPath(rcloneExecutablePath), stagedPath, configPasswordCommand, cancellationToken)
                .ConfigureAwait(false);

            var remotes = await session.ListRemotesAsync(cancellationToken).ConfigureAwait(false);
            if (remotes.Contains(remoteName, StringComparer.OrdinalIgnoreCase))
            {
                return Result.Failure<RcloneConfigMutationResult>(
                    "rclone.config_remote_exists",
                    $"The rclone remote '{remoteName}' already exists.");
            }

            await createRemote(session, cancellationToken).ConfigureAwait(false);
            var updatedRemotes = await session.ListRemotesAsync(cancellationToken).ConfigureAwait(false);
            if (!updatedRemotes.Contains(remoteName, StringComparer.OrdinalIgnoreCase))
            {
                return Result.Failure<RcloneConfigMutationResult>(
                    "rclone.config_create_unverified",
                    "rclone did not report the newly created remote.");
            }
            retainStage = true;
            return Result.Success(new RcloneConfigMutationResult(stagedPath, remoteName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<RcloneConfigMutationResult>(
                "rclone.rc_timeout", "The isolated rclone request timed out.", true);
        }
        catch (TimeoutException exception)
        {
            return Result.Failure<RcloneConfigMutationResult>("rclone.rc_timeout", exception.Message, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or HttpRequestException or JsonException)
        {
            return Result.Failure<RcloneConfigMutationResult>("rclone.config_mutation", exception.Message);
        }
        finally
        {
            if (!retainStage)
                TryDelete(stagedPath);
        }
    }

    internal static OperationResult Validate(
        string executablePath,
        string configPath,
        string configPassword,
        RcloneWebDavRemoteCreateRequest? request)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return Result.Failure("rclone.executable", "The rclone executable does not exist.");
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return Result.Failure("rclone.config", "The existing rclone configuration does not exist.");
        if (string.IsNullOrEmpty(configPassword) || configPassword.Length > 4096 || ContainsControl(configPassword))
            return Result.Failure("rclone.config_password", "The rclone configuration password is invalid.");
        if (request is null || !RemoteNamePattern.IsMatch(request.RemoteName) || request.RemoteName.EndsWith(' '))
            return Result.Failure("rclone.remote_name", "The rclone remote name is invalid.");
        if (!request.Endpoint.IsAbsoluteUri || !string.Equals(request.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(request.Endpoint.UserInfo) || !string.IsNullOrEmpty(request.Endpoint.Query) ||
            !string.IsNullOrEmpty(request.Endpoint.Fragment))
            return Result.Failure("rclone.remote_endpoint", "The WebDAV endpoint must be an HTTPS URL without credentials, query, or fragment.");
        if (!VendorPattern.IsMatch(request.Vendor))
            return Result.Failure("rclone.remote_vendor", "The WebDAV vendor is invalid.");
        if (!ValidText(request.Username, 1024) || !ValidText(request.Password, 2048))
            return Result.Failure("rclone.remote_credentials", "The WebDAV credentials are invalid.");
        return Result.Success();
    }

    internal static OperationResult Validate(
        string executablePath,
        string configPath,
        string configPassword,
        RcloneSftpKeyFileRemoteCreateRequest? request)
    {
        var common = ValidateCommon(
            executablePath,
            configPath,
            configPassword,
            request?.RemoteName,
            request?.Username,
            "key-file-authentication");
        if (!common.Succeeded)
            return common;

        var connection = request!.Connection;
        var connectionValidation = ValidateSftpConnection(connection);
        if (!connectionValidation.Succeeded)
            return connectionValidation;
        if (connection.Authentication != SftpAuthenticationMethod.PrivateKey)
            return Result.Failure("rclone.sftp_authentication", "Private key authentication is not selected.");
        if (string.IsNullOrWhiteSpace(request.KeyFilePath) || request.KeyFilePath.Length > 1_024 ||
            !Path.IsPathFullyQualified(request.KeyFilePath) || !File.Exists(request.KeyFilePath))
            return Result.Failure("rclone.sftp_key_file", "The SFTP private key file does not exist.");
        if (request.KeyFilePassphrase.Length > 2_048 || ContainsControl(request.KeyFilePassphrase))
            return Result.Failure("rclone.sftp_key_passphrase", "The private key passphrase is invalid.");
        return Result.Success();
    }

    private static OperationResult ValidateSftpConnection(SftpConnectionDefinition? connection)
    {
        if (connection is null || !SetupProfileValidator.IsValidSftpHost(connection.Host))
            return Result.Failure("rclone.sftp_host", "The SFTP host is invalid.");
        if (connection.Port is < 1 or > 65_535)
            return Result.Failure("rclone.sftp_port", "The SFTP port is invalid.");
        if (!string.IsNullOrEmpty(connection.KnownHost))
        {
            var hostKey = connection.KnownHost.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (hostKey.Length != 2 || !SftpHostKeyAlgorithms.Contains(hostKey[0]) || !ValidSftpPublicKey(hostKey[1]))
                return Result.Failure("rclone.sftp_host_key", "The pinned SFTP server host key is invalid.");
        }
        return Result.Success();
    }

    internal static OperationResult Validate(
        string executablePath,
        string configPath,
        string configPassword,
        RcloneSftpPasswordRemoteCreateRequest? request)
    {
        var common = ValidateCommon(executablePath, configPath, configPassword, request?.RemoteName,
            request?.Username, request?.Password);
        if (!common.Succeeded) return common;
        var connection = request!.Connection;
        var connectionValidation = ValidateSftpConnection(connection);
        if (!connectionValidation.Succeeded)
            return connectionValidation;
        if (connection.Authentication != SftpAuthenticationMethod.Password)
            return Result.Failure("rclone.sftp_authentication", "Password authentication is not selected.");
        return Result.Success();
    }

    private static OperationResult ValidateCommon(
        string executablePath, string configPath, string configPassword,
        string? remoteName, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return Result.Failure("rclone.executable", "The rclone executable does not exist.");
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return Result.Failure("rclone.config", "The existing rclone configuration does not exist.");
        if (string.IsNullOrEmpty(configPassword) || configPassword.Length > 4096 || ContainsControl(configPassword))
            return Result.Failure("rclone.config_password", "The rclone configuration password is invalid.");
        if (remoteName is null || !RemoteNamePattern.IsMatch(remoteName) || remoteName.EndsWith(' '))
            return Result.Failure("rclone.remote_name", "The rclone remote name is invalid.");
        if (username is null || password is null || !ValidText(username, 1024) || !ValidText(password, 2048))
            return Result.Failure("rclone.remote_credentials", "The remote credentials are invalid.");
        return Result.Success();
    }

    private static bool ValidSftpPublicKey(string value)
    {
        if (value.Length is < 40 or > 24_000) return false;
        try { return Convert.FromBase64String(value).Length is >= 32 and <= 16_384; }
        catch (FormatException) { return false; }
    }

    internal static string CreateStagingPath(string configPath) =>
        Path.GetFullPath(configPath) + $".{Guid.NewGuid():N}.mutation-stage";

    private static bool ValidText(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !ContainsControl(value);
    private static bool ContainsControl(string value) => value.Any(char.IsControl);
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

internal interface IRcloneRcSessionFactory
{
    Task<IRcloneRcSession> StartAsync(string executablePath, string stagedConfigPath, string configPasswordCommand, CancellationToken token);
}

internal interface IRcloneRcSession : IAsyncDisposable
{
    Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken token);
    Task<IReadOnlyDictionary<string, string>> GetConfigAsync(string remoteName, CancellationToken token) =>
        throw new NotSupportedException("Reading remote configuration is not supported by this session.");
    Task CreateWebDavRemoteAsync(RcloneWebDavRemoteCreateRequest request, CancellationToken token);
    Task CreateSftpPasswordRemoteAsync(RcloneSftpPasswordRemoteCreateRequest request, CancellationToken token);
    Task CreateSftpKeyFileRemoteAsync(RcloneSftpKeyFileRemoteCreateRequest request, CancellationToken token);
}

internal sealed partial class RcloneRcSessionFactory : IRcloneRcSessionFactory
{
    public async Task<IRcloneRcSession> StartAsync(
        string executablePath, string stagedConfigPath, string configPasswordCommand, CancellationToken token)
    {
        var user = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = CreateStartInfo(executablePath, stagedConfigPath, configPasswordCommand, user, password);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start the isolated rclone control process.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        Uri endpoint;
        try
        {
            endpoint = await ReadEndpointAsync(process, token).ConfigureAwait(false);
        }
        catch
        {
            ProcessTermination.TryKillTree(process);
            await ProcessTermination.WaitForExitAsync(
                    process,
                    TimeSpan.FromSeconds(3),
                    CancellationToken.None)
                .ConfigureAwait(false);
            process.Dispose();
            throw;
        }
        var session = new RcloneRcSession(process, endpoint, user, password);
        try
        {
            await session.WaitUntilReadyAsync(token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath, string stagedConfigPath, string configPasswordCommand, string user, string password)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "rcd", "--config", stagedConfigPath, "--ask-password=false",
            "--password-command", configPasswordCommand,
            "--rc-addr", "127.0.0.1:0", "--rc-user", user, "--rc-pass", password
        }) startInfo.ArgumentList.Add(argument);
        foreach (var argument in RcloneUserAgentArguments.Create([]))
            startInfo.ArgumentList.Add(argument);
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(key, "RCLONE_USER_AGENT", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            startInfo.Environment.Remove(key);
        startInfo.Environment.Remove("_RCLONE_CONFIG_KEY_FILE");
        return startInfo;
    }

    internal static bool TryParseLoopbackEndpoint(string? line, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var match = LoopbackEndpointPattern().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port) || port is <= 0 or > 65535 ||
            !Uri.TryCreate($"http://127.0.0.1:{port}/", UriKind.Absolute, out var parsed) || parsed is null) return false;
        endpoint = parsed;
        return true;
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:(\d{1,5})/", RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackEndpointPattern();

    private static async Task<Uri> ReadEndpointAsync(Process process, CancellationToken token)
    {
        var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        DataReceivedEventHandler inspect = (_, eventArgs) =>
        {
            if (TryParseLoopbackEndpoint(eventArgs.Data, out var parsed)) endpoint.TrySetResult(parsed);
        };
        process.OutputDataReceived += inspect;
        process.ErrorDataReceived += inspect;
        process.Exited += (_, _) => endpoint.TrySetException(
            new InvalidOperationException($"rclone control process exited with code {process.ExitCode}."));
        if (process.HasExited)
            throw new InvalidOperationException($"rclone control process exited with code {process.ExitCode}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            return await endpoint.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The isolated rclone control process did not report its endpoint.");
        }
    }
}

internal sealed class RcloneRcSession : IRcloneRcSession
{
    private const int MaximumConfigResponseBytes = 64 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly HttpClient _client;

    public RcloneRcSession(Process process, Uri endpoint, string user, string password)
    {
        _process = process;
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = RequestTimeout
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public async Task WaitUntilReadyAsync(CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        while (true)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"rclone control process exited with code {_process.ExitCode}.");
            try
            {
                using var response = await PostAsync("core/version", new { }, linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException) when (!linked.IsCancellationRequested)
            {
                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new TimeoutException("The isolated rclone control process did not become ready.");
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken token)
    {
        using var response = await PostSensitiveAsync("config/listremotes", new { }, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        await response.Content.LoadIntoBufferAsync(MaximumConfigResponseBytes, timeout.Token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<ListRemotesResponse>(stream, cancellationToken: timeout.Token)
            .ConfigureAwait(false);
        if (result?.Remotes is null || result.Remotes.Count > 1024 ||
            result.Remotes.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)))
            throw new JsonException("rclone returned an invalid remote list.");
        return result.Remotes;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetConfigAsync(string remoteName, CancellationToken token)
    {
        using var response = await PostSensitiveAsync("config/get", new { name = remoteName }, token)
            .ConfigureAwait(false);
        return await ReadConfigResponseAsync(response, token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<string, string>> ReadConfigResponseAsync(
        HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The isolated rclone configuration read failed.");
        if (response.Content.Headers.ContentLength > MaximumConfigResponseBytes)
            throw new InvalidOperationException("The isolated rclone configuration response is too large.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        var bytes = new byte[MaximumConfigResponseBytes + 1];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumConfigResponseBytes)
                throw new InvalidOperationException("The isolated rclone configuration response is too large.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (result.Count >= 256 || property.Name.Length is 0 or > 128 || property.Name.Any(char.IsControl) ||
                    property.Value.ValueKind != JsonValueKind.String || !result.TryAdd(property.Name, property.Value.GetString()!))
                    throw new JsonException();
            }
            return result;
        }
        catch (JsonException)
        {
            // JSON exception paths can contain credential values or configuration keys.
            throw new InvalidOperationException("The isolated rclone configuration response is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<HttpResponseMessage> PostSensitiveAsync<T>(string path, T payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task CreateWebDavRemoteAsync(RcloneWebDavRemoteCreateRequest request, CancellationToken token)
    {
        var payload = new
        {
            name = request.RemoteName,
            type = "webdav",
            parameters = new { url = request.Endpoint.AbsoluteUri, vendor = request.Vendor, user = request.Username, pass = request.Password },
            opt = new { obscure = true, nonInteractive = true }
        };
        using var response = await PostAsync("config/create", payload, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateSftpPasswordRemoteAsync(
        RcloneSftpPasswordRemoteCreateRequest request,
        CancellationToken token)
    {
        var payload = new
        {
            name = request.RemoteName,
            type = "sftp",
            parameters = new
            {
                host = request.Connection.Host,
                port = request.Connection.Port,
                user = request.Username,
                pass = request.Password,
                host_keys = request.Connection.KnownHost
            },
            opt = new { obscure = true, nonInteractive = true }
        };
        using var response = await PostAsync("config/create", payload, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateSftpKeyFileRemoteAsync(
        RcloneSftpKeyFileRemoteCreateRequest request,
        CancellationToken token)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["host"] = request.Connection.Host,
            ["port"] = request.Connection.Port,
            ["user"] = request.Username,
            ["key_file"] = request.KeyFilePath
        };
        if (!string.IsNullOrEmpty(request.KeyFilePassphrase))
            parameters["key_file_pass"] = request.KeyFilePassphrase;
        if (!string.IsNullOrEmpty(request.Connection.KnownHost))
            parameters["host_keys"] = request.Connection.KnownHost;
        var payload = new
        {
            name = request.RemoteName,
            type = "sftp",
            parameters,
            opt = new { obscure = true, nonInteractive = true }
        };
        using var response = await PostAsync("config/create", payload, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(ShutdownTimeout);
                try
                {
                    using var response = await PostAsync("core/quit", new { }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { TryKill(); }
            }
        }
        finally
        {
            if (!ProcessTermination.HasExitedOrUnavailable(_process))
            {
                ProcessTermination.TryKillTree(_process);
                await ProcessTermination.WaitForExitAsync(
                        _process,
                        TimeSpan.FromSeconds(3),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            _client.Dispose();
            _process.Dispose();
        }
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T payload, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PostAsync(path, content, token).ConfigureAwait(false);
    }

    private void TryKill()
    {
        ProcessTermination.TryKillTree(_process);
    }

    private sealed record ListRemotesResponse(
        [property: JsonPropertyName("remotes")] IReadOnlyList<string> Remotes);
}
