using System.Net;
using ResoDrive.Core.Results;
using ResoDrive.Core.Setup;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneConfigMutationServiceTests : IDisposable
{
    private const string SftpPublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rdrive-mutation-{Guid.NewGuid():N}");

    public RcloneConfigMutationServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Validate_AcceptsClosedWebDavRequest()
    {
        var executable = Write("rclone.exe", "x");
        var config = Write("rclone.conf", "encrypted");
        var result = RcloneConfigMutationService.Validate(executable, config, "config-secret",
            new RcloneWebDavRemoteCreateRequest(
                "remote-1", new Uri("https://example.test/dav/"), "other", "user", "app-password"));
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" remote")]
    [InlineData("remote:")]
    [InlineData("remote/child")]
    [InlineData("remote\nname")]
    public void Validate_RejectsUnsafeRemoteNames(string remoteName)
    {
        var result = Validate(new(remoteName, new Uri("https://example.test/dav/"), "other", "user", "password"));
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.remote_name", result.Error?.Code);
    }

    [Fact]
    public void Validate_RejectsEndpointCredentialsQueryAndFragment()
    {
        var result = Validate(new("remote", new Uri("https://user@example.test/dav?q=1#x"), "other", "user", "password"));
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.remote_endpoint", result.Error?.Code);
    }

    [Fact]
    public void StagingPath_IsAdjacentAndDistinct()
    {
        var config = Path.Combine(_root, "rclone.conf");
        var staged = RcloneConfigMutationService.CreateStagingPath(config);
        Assert.Equal(_root, Path.GetDirectoryName(staged));
        Assert.NotEqual(config, staged);
        Assert.EndsWith(".mutation-stage", staged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NOTICE: Serving remote control on http://127.0.0.1:49152/", 49152)]
    [InlineData("http://127.0.0.1:1/", 1)]
    public void EndpointParser_AcceptsOnlyReportedLoopbackHttp(string line, int port)
    {
        Assert.True(RcloneRcSessionFactory.TryParseLoopbackEndpoint(line, out var endpoint));
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(endpoint.Host));
    }

    [Theory]
    [InlineData("http://0.0.0.0:1234/")]
    [InlineData("http://127.0.0.1:0/")]
    [InlineData("https://127.0.0.1:1234/")]
    [InlineData("http://127.0.0.1:99999/")]
    public void EndpointParser_RejectsUntrustedEndpoint(string line) =>
        Assert.False(RcloneRcSessionFactory.TryParseLoopbackEndpoint(line, out _));

    [Fact]
    public void ProcessArguments_ContainNoConfigPassword()
    {
        const string passwordCommand = "\"resodrive.exe\" password";
        var startInfo = RcloneRcSessionFactory.CreateStartInfo(
            @"C:\rclone.exe", @"C:\stage\rclone.conf", passwordCommand, "ephemeral-user", "ephemeral-auth");

        Assert.Contains("--password-command", startInfo.ArgumentList);
        Assert.Contains(passwordCommand, startInfo.ArgumentList);
        Assert.False(startInfo.Environment.ContainsKey("RCLONE_CONFIG_PASS"));
        Assert.DoesNotContain(startInfo.Environment.Keys,
            key => key.StartsWith("RCLONE_CONFIG_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigurationProcess_IdentifiesResoDriveUnlessUserAgentIsOverridden()
    {
        var startInfo = RcloneRcSessionFactory.CreateStartInfo(
            @"C:\rclone.exe", @"C:\stage\rclone.conf", "password-command", "ephemeral-user", "ephemeral-auth");

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RCLONE_USER_AGENT")))
        {
            var optionIndex = startInfo.ArgumentList.IndexOf("--user-agent");
            Assert.True(optionIndex >= 0);
            Assert.Equal(ClientUserAgent.Value, startInfo.ArgumentList[optionIndex + 1]);
        }
        else
        {
            Assert.DoesNotContain("--user-agent", startInfo.ArgumentList);
        }
    }

    [Fact]
    public async Task Collision_IsExact_AndFailedMutationDeletesStageWithoutWritingLiveFile()
    {
        var executable = Write("rclone.exe", "x");
        var config = Write("rclone.conf", "live");
        var fake = new FakeSession(["Remote"]);
        var service = new RcloneConfigMutationService(new FakeFactory(fake));
        var request = new RcloneWebDavRemoteCreateRequest(
            "Remote", new Uri("https://example.test/dav/"), "other", "user", "password");

        var result = await service.AppendWebDavRemoteAsync(executable, config, "secret", request);

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.config_remote_exists", result.Error?.Code);
        Assert.Equal("live", File.ReadAllText(config));
        Assert.Empty(Directory.GetFiles(_root, "*.mutation-stage"));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task Success_ReturnsStageAndLeavesLiveFileUntouched()
    {
        var executable = Write("rclone.exe", "x");
        var config = Write("rclone.conf", "live");
        var fake = new FakeSession([], ["Remote"]);
        var service = new RcloneConfigMutationService(new FakeFactory(fake));
        var request = new RcloneWebDavRemoteCreateRequest(
            "Remote", new Uri("https://example.test/dav/"), "other", "user", "password");

        var result = await service.AppendWebDavRemoteAsync(executable, config, "secret", request);

        Assert.True(result.Succeeded);
        Assert.Equal("live", File.ReadAllText(config));
        Assert.True(File.Exists(result.Value!.StagedConfigPath));
        Assert.Same(request, fake.Created);
        Assert.True(fake.Disposed);
        File.Delete(result.Value.StagedConfigPath);
    }

    [Fact]
    public async Task SftpPassword_UsesPinnedHostKeyAndClosedTypedRequest()
    {
        var executable = Write("rclone.exe", "x");
        var config = Write("rclone.conf", "live");
        var fake = new FakeSession([], ["Server"]);
        var service = new RcloneConfigMutationService(new FakeFactory(fake));
        var connection = new SftpConnectionDefinition
        {
            Host = "files.example.test",
            Port = 2222,
            KnownHost = $"ssh-ed25519 {SftpPublicKey}"
        };
        var request = new RcloneSftpPasswordRemoteCreateRequest("Server", connection, "user", "password");

        var result = await service.AppendSftpPasswordRemoteAsync(executable, config, "secret", request);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Same(request, fake.CreatedSftp);
        Assert.Equal("live", File.ReadAllText(config));
        File.Delete(result.Value!.StagedConfigPath);
    }

    [Theory]
    [InlineData(0, "ssh-ed25519 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData(22, "ssh-dss AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void SftpPassword_RejectsInvalidPortOrPinnedHostKey(int port, string knownHost)
    {
        var request = new RcloneSftpPasswordRemoteCreateRequest(
            "Server",
            new SftpConnectionDefinition { Host = "files.example.test", Port = port, KnownHost = knownHost },
            "user",
            "password");

        var result = RcloneConfigMutationService.Validate(
            Write("rclone.exe", "x"), Write("rclone.conf", "x"), "secret", request);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SftpPrivateKey_UsesExistingKeyFileWithoutPasswordCredential()
    {
        var executable = Write("rclone.exe", "x");
        var config = Write("rclone.conf", "live");
        var keyFile = Write("id_rsa", "private-key-placeholder");
        var fake = new FakeSession([], ["Server"]);
        var service = new RcloneConfigMutationService(new FakeFactory(fake));
        var connection = new SftpConnectionDefinition
        {
            Host = "files.example.test",
            Authentication = SftpAuthenticationMethod.PrivateKey
        };
        var request = new RcloneSftpKeyFileRemoteCreateRequest(
            "Server", connection, "user", keyFile, string.Empty);

        var result = await service.AppendSftpKeyFileRemoteAsync(
            executable, config, "secret", request);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Same(request, fake.CreatedSftpKey);
        File.Delete(result.Value!.StagedConfigPath);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private OperationResult Validate(RcloneWebDavRemoteCreateRequest request) =>
        RcloneConfigMutationService.Validate(Write("rclone.exe", "x"), Write("rclone.conf", "x"), "secret", request);
    private string Write(string name, string text) { var path = Path.Combine(_root, name); File.WriteAllText(path, text); return path; }

    private sealed class FakeFactory(IRcloneRcSession session) : IRcloneRcSessionFactory
    {
        public Task<IRcloneRcSession> StartAsync(string executablePath, string stagedConfigPath, string configPassword, CancellationToken token) => Task.FromResult(session);
    }
    private sealed class FakeSession(
        IReadOnlyList<string> remotes,
        IReadOnlyList<string>? remotesAfterCreate = null) : IRcloneRcSession
    {
        private bool _created;
        public bool Disposed { get; private set; }
        public RcloneWebDavRemoteCreateRequest? Created { get; private set; }
        public RcloneSftpPasswordRemoteCreateRequest? CreatedSftp { get; private set; }
        public RcloneSftpKeyFileRemoteCreateRequest? CreatedSftpKey { get; private set; }
        public Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken token) =>
            Task.FromResult(_created ? remotesAfterCreate ?? remotes : remotes);
        public Task CreateWebDavRemoteAsync(RcloneWebDavRemoteCreateRequest request, CancellationToken token)
        {
            Created = request;
            _created = true;
            return Task.CompletedTask;
        }
        public Task CreateSftpPasswordRemoteAsync(RcloneSftpPasswordRemoteCreateRequest request, CancellationToken token)
        {
            CreatedSftp = request;
            _created = true;
            return Task.CompletedTask;
        }
        public Task CreateSftpKeyFileRemoteAsync(RcloneSftpKeyFileRemoteCreateRequest request, CancellationToken token)
        {
            CreatedSftpKey = request;
            _created = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
