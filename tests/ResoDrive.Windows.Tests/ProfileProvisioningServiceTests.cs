using System.Net;
using System.Security.Cryptography;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;
using ResoDrive.Core.Setup;

namespace ResoDrive.Windows.Tests;

public sealed class ProfileProvisioningServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rdrive-provision-{Guid.NewGuid():N}");

    [Fact]
    public void AllocateRemoteName_IsCaseInsensitiveAndUsesFirstFreeSuffix()
    {
        var result = ProfileProvisioningService.AllocateRemoteName(
            "Storage", ["storage", "STORAGE 2", "Storage 4"]);

        Assert.Equal("Storage 3", result);
    }

    [Fact]
    public void AllocateRemoteName_KeepsRcloneMaximumLengthWhenSuffixing()
    {
        var preferred = new string('A', 128);
        var result = ProfileProvisioningService.AllocateRemoteName(preferred, [preferred]);

        Assert.Equal(128, result.Length);
        Assert.EndsWith(" 2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeExactPassword_PreservesExactValue()
    {
        const string password = "  pä ss-word  ";
        Assert.Equal(password, ProfileProvisioningService.NormalizeExactPassword(password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello\nworld")]
    [InlineData("hello\0world")]
    public void NormalizeExactPassword_RejectsUnsafeValues(string password) =>
        Assert.Throws<ArgumentException>(() => ProfileProvisioningService.NormalizeExactPassword(password));

    [Fact]
    public void NormalizeOptionalSecret_AllowsPasswordlessPrivateKey() =>
        Assert.Equal(string.Empty, ProfileProvisioningService.NormalizeOptionalSecret(string.Empty));

    [Fact]
    public void ValidateSftpKeyFile_AcceptsExistingRegularFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "id_rsa");
        File.WriteAllText(path, "private-key-placeholder");

        Assert.Equal(path, ProfileProvisioningService.ValidateSftpKeyFile(path));
    }

    [Fact]
    public void ValidateSftpKeyFile_RejectsMissingFile()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<ArgumentException>(() =>
            ProfileProvisioningService.ValidateSftpKeyFile(Path.Combine(_root, "missing-key")));
    }

    [Fact]
    public async Task ExistingConfigWithoutProtectedSecret_IsPreservedAndNeverMutated()
    {
        Directory.CreateDirectory(_root);
        var paths = new ApplicationPaths(_root);
        await File.WriteAllTextAsync(paths.ConfigFile, "encrypted-existing-config");
        paths.EnsureCreated();
        var executable = paths.RcloneExecutable;
        await File.WriteAllTextAsync(executable, string.Empty);
        var mutation = new RecordingMutationService();
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.MultiStatus));
        var service = new ProfileProvisioningService(
            paths,
            Catalog,
            new RcloneRuntimeLocator(paths, new SuccessfulLocatorRunner()),
            new MissingSecretStore(),
            mutation,
            new RecordingProfileRunner(),
            http);

        var result = await service.ProvisionAsync(Request(), "app-password");

        Assert.False(result.Succeeded);
        Assert.Equal("setup.config_secret_missing", result.Error?.Code);
        Assert.False(mutation.WasCalled);
        Assert.Equal("encrypted-existing-config", await File.ReadAllTextAsync(paths.ConfigFile));
    }

    [Fact]
    public async Task ExistingConfigWithUnreadableProtectedSecret_IsPreservedAndNeverMutated()
    {
        Directory.CreateDirectory(_root);
        var paths = new ApplicationPaths(_root);
        await File.WriteAllTextAsync(paths.ConfigFile, "encrypted-existing-config");
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.RcloneExecutable, string.Empty);
        var mutation = new RecordingMutationService();
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.MultiStatus));
        var service = new ProfileProvisioningService(
            paths, Catalog,
            new RcloneRuntimeLocator(paths, new SuccessfulLocatorRunner()),
            new ThrowingSecretStore(), mutation, new RecordingProfileRunner(), http);

        var result = await service.ProvisionAsync(Request(), "app-password");

        Assert.False(result.Succeeded);
        Assert.Equal("setup.config_secret_unreadable", result.Error?.Code);
        Assert.False(mutation.WasCalled);
        Assert.Equal("encrypted-existing-config", await File.ReadAllTextAsync(paths.ConfigFile));
    }

    private static ProfileSetupRequest Request() => new()
    {
        ProfileId = "default",
        Username = "traveler",
        DisplayName = "Travel files",
        DriveLetter = 'U',
        StartWithWindows = true
    };

    private static ISetupProfileCatalog Catalog { get; } = new SetupProfileCatalog(
        [new SetupProfile
        {
            Id = "default",
            DisplayName = "Test storage",
            Description = "Test profile",
            RemoteName = "Storage",
            Connection = new WebDavConnectionDefinition
            {
                BaseUrl = new Uri("https://storage.example.org/"),
                PathTemplate = "/dav/{username}",
                Vendor = WebDavVendor.Nextcloud
            }
        }],
        ProfileCatalogSource.AdjacentFile);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SuccessfulLocatorRunner : IRcloneProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken,
            Action<string>? standardErrorLineReceived = null) =>
            Task.FromResult(new ProcessRunResult(0, "rclone v1.75.0\n", string.Empty, false));
    }

    private sealed class RecordingProfileRunner : IProfileRcloneRunner
    {
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, IReadOnlyDictionary<string, string>? environment, string? standardInput,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
    }

    private sealed class MissingSecretStore : IConfigSecretStore
    {
        public bool Exists => false;
        public Task<string> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Must not load a missing secret.");
        public Task SaveProtectedFileAsync(string password, string destinationPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Must not replace a missing secret for an existing config.");
    }

    private sealed class ThrowingSecretStore : IConfigSecretStore
    {
        public bool Exists => true;
        public Task<string> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new CryptographicException("corrupt");
        public Task SaveProtectedFileAsync(string password, string destinationPath,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class RecordingMutationService : IRcloneConfigMutationService
    {
        public bool WasCalled { get; private set; }
        public Task<OperationResult<RcloneConfigMutationResult>> AppendWebDavRemoteAsync(
            string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
            RcloneWebDavRemoteCreateRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Mutation was not expected.");
        }
        public Task<OperationResult<RcloneConfigMutationResult>> AppendSftpPasswordRemoteAsync(
            string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
            RcloneSftpPasswordRemoteCreateRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Mutation was not expected.");
        }
        public Task<OperationResult<RcloneConfigMutationResult>> AppendSftpKeyFileRemoteAsync(
            string rcloneExecutablePath, string liveConfigPath, string configPassword, string configPasswordCommand,
            RcloneSftpKeyFileRemoteCreateRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Mutation was not expected.");
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ClientUserAgent.Value, request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
