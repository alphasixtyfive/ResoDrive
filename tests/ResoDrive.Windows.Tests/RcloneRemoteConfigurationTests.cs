using System.Net;
using System.Text;

namespace ResoDrive.Windows.Tests;

[Collection(RcloneRuntimeMutationTestGroup.Name)]
public sealed class RcloneRemoteConfigurationTests : IDisposable
{
    private const string Encrypted = "# Encrypted rclone configuration File\n\nRCLONE_ENCRYPT_V0:\nfixture-ciphertext";
    private const string PotatoObscured = "YWFhYWFhYWFhYWFhYWFhYXMaGgIlEQ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rdrive-config-access-{Guid.NewGuid():N}");
    public RcloneRemoteConfigurationTests() => Directory.CreateDirectory(_root);

    // The first three are upstream vectors; the longer and Unicode vectors came from rclone v1.75.0.
    [Theory]
    [InlineData("YWFhYWFhYWFhYWFhYWFhYQ", "")]
    [InlineData(PotatoObscured, "potato")]
    [InlineData("YmJiYmJiYmJiYmJiYmJiYp3gcEWbAw", "potato")]
    [InlineData("osB9Qx3SscKOqqqKsfge_Rg_DUxCcWtsZz837WWNfZNl25hTeYS3TIGOpFUQJ6nEdDujRSG76wgnxtqLe0DFBDrluxs", "disposable-replacement-password-with-multiple-blocks")]
    [InlineData("vdBt52e4C7OnfEsrdg79uJwkXQTezHK1dtCXFbnNHeQV", "café-東京-🔒")]
    public void Reveal_MatchesRcloneKnownAnswers(string obscured, string expected) =>
        Assert.Equal(expected, RcloneObscuredSecret.Reveal(obscured));

    [Theory]
    [InlineData("")]
    [InlineData("plaintext-password")]
    [InlineData("aGVsbG8")]
    [InlineData("YmJiYmJiYmJiYmJiYmJiYp*gcEWbAw")]
    [InlineData(PotatoObscured + "==")]
    [InlineData(PotatoObscured + "\n")]
    [InlineData("YWFhYWFhYWFhYWFhYWFhYXMaGgIlER")]
    public void Reveal_RejectsInvalidEncodingWithoutExposingInput(string obscured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RcloneObscuredSecret.Reveal(obscured));
        Assert.Equal("The stored rclone password is not a valid obscured credential.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)10)]
    [InlineData((byte)127)]
    [InlineData((byte)255)]
    public void Reveal_RejectsControlsAndInvalidUtf8(byte firstPlaintextByte)
    {
        // CTR ciphertext bit flip changes the first known plaintext byte without using our decoder.
        var bytes = Convert.FromBase64String(PotatoObscured + "==");
        bytes[16] ^= (byte)('p' ^ firstPlaintextByte);
        var malformed = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<InvalidOperationException>(() => RcloneObscuredSecret.Reveal(malformed));
    }

    [Fact]
    public void Reveal_RejectsOversizedInput() =>
        Assert.Throws<InvalidOperationException>(() => RcloneObscuredSecret.Reveal(new string('a', 12000)));

    [Fact]
    public void IsolatedProcess_RemovesInheritedLoggingAuthenticationAndKeyFileOverrides()
    {
        string[] names = ["RCLONE_LOG_FILE", "RCLONE_DUMP", "RCLONE_RC_NO_AUTH", "RCLONE_CONFIG_PASS", "_RCLONE_CONFIG_KEY_FILE"];
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "fixture-override");
            var start = RcloneRcSessionFactory.CreateStartInfo("rclone.exe", "fixture.conf", "password-command", "user", "secret");
            foreach (var name in names) Assert.False(start.Environment.ContainsKey(name));
            Assert.DoesNotContain("fixture-override", start.ArgumentList);
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [Fact]
    public async Task Read_QueriesOnlySelectedRemotesAndLeavesConfigUntouched()
    {
        var executable = Write("rclone.exe", "fixture");
        var config = Write("rclone.conf", Encrypted);
        var session = new FakeSession();
        var factory = new FakeFactory(session);
        var access = new RcloneRemoteConfigurationAccess(factory);

        var result = await access.ReadAsync(executable, config, "password-command", ["cloud", "missing", "other", "cloud"], default);

        Assert.Equal(["cloud", "other"], session.ReadNames);
        Assert.Equal(["cloud", "other"], result.Keys);
        Assert.Equal("nextcloud", result["cloud"]["vendor"]);
        Assert.Equal(config, factory.ConfigPath);
        Assert.Equal("password-command", factory.PasswordCommand);
        Assert.Equal(Encrypted, File.ReadAllText(config));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Read_EmptySelectionDoesNotStartProcess()
    {
        var factory = new FakeFactory(new FakeSession());
        var access = new RcloneRemoteConfigurationAccess(factory);
        var result = await access.ReadAsync(Write("rclone.exe", "fixture"), Write("rclone.conf", Encrypted),
            "password-command", [], default);
        Assert.Empty(result);
        Assert.Null(factory.ConfigPath);
    }

    [Fact]
    public async Task Read_RequestTimeoutIsRecoverableAndDisposesSession()
    {
        var session = new FakeSession { TimeoutRead = true };
        var access = new RcloneRemoteConfigurationAccess(new FakeFactory(session));
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => access.ReadAsync(
            Write("rclone.exe", "fixture"), Write("rclone.conf", Encrypted), "password-command", ["cloud"], default));
        Assert.Equal("The isolated rclone configuration read timed out.", exception.Message);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Read_CallerCancellationDoesNotStartProcess()
    {
        var factory = new FakeFactory(new FakeSession());
        var access = new RcloneRemoteConfigurationAccess(factory);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.ReadAsync(
            Write("rclone.exe", "fixture"), Write("rclone.conf", Encrypted), "password-command", ["cloud"], cancellation.Token));
        Assert.Null(factory.ConfigPath);
    }

    [Theory]
    [InlineData("[cloud]\npass = plaintext")]
    [InlineData("# RCLONE_ENCRYPT_V0:\n[cloud]\npass = plaintext")]
    [InlineData("RCLONE_ENCRYPT_V1:\npayload")]
    public async Task Read_RejectsUnencryptedOrUnsupportedConfigBeforeProcessStart(string configText)
    {
        var factory = new FakeFactory(new FakeSession());
        var access = new RcloneRemoteConfigurationAccess(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => access.ReadAsync(
            Write("rclone.exe", "fixture"), Write("rclone.conf", configText), "password-command", ["cloud"], default));
        Assert.Null(factory.ConfigPath);
    }

    [Fact]
    public async Task ResponseParser_AcceptsStringConfiguration()
    {
        using var response = Response("{\"type\":\"webdav\",\"pass\":\"obscured\",\"vendor\":\"nextcloud\"}");
        var values = await RcloneRcSession.ReadConfigResponseAsync(response, default);
        Assert.Equal("webdav", values["type"]);
        Assert.Equal("obscured", values["pass"]);
    }

    [Theory]
    [InlineData("{\"pass\":\"secret\",\"pass\":\"other\"}")]
    [InlineData("{\"pass\":{\"sensitive-key\":\"secret\"}}")]
    [InlineData("{\"pass\":null}")]
    [InlineData("[\"secret\"]")]
    [InlineData("{\"secret\":")]
    public async Task ResponseParser_RejectsMalformedOrAmbiguousConfigWithoutSecretErrors(string body)
    {
        using var response = Response(body);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RcloneRcSession.ReadConfigResponseAsync(response, default));
        Assert.Equal("The isolated rclone configuration response is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ResponseParser_BoundsResponseWithNoContentLength()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[65537]))
        };
        response.Content.Headers.ContentLength = null;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RcloneRcSession.ReadConfigResponseAsync(response, default));
        Assert.Equal("The isolated rclone configuration response is too large.", exception.Message);
    }

    [Fact]
    public async Task ResponseParser_DoesNotReadErrorBody()
    {
        using var response = Response("private credentials from failed config/get");
        response.StatusCode = HttpStatusCode.BadRequest;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RcloneRcSession.ReadConfigResponseAsync(response, default));
        Assert.Equal("The isolated rclone configuration read failed.", exception.Message);
    }

    private static HttpResponseMessage Response(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeFactory(FakeSession session) : IRcloneRcSessionFactory
    {
        public string? ConfigPath { get; private set; }
        public string? PasswordCommand { get; private set; }
        public Task<IRcloneRcSession> StartAsync(string executablePath, string stagedConfigPath, string configPasswordCommand, CancellationToken token)
        {
            ConfigPath = stagedConfigPath;
            PasswordCommand = configPasswordCommand;
            return Task.FromResult<IRcloneRcSession>(session);
        }
    }

    private sealed class FakeSession : IRcloneRcSession
    {
        public bool TimeoutRead { get; init; }
        public List<string> ReadNames { get; } = [];
        public bool Disposed { get; private set; }
        public Task<IReadOnlyDictionary<string, string>> GetConfigAsync(string remoteName, CancellationToken token)
        {
            if (TimeoutRead) throw new OperationCanceledException();
            ReadNames.Add(remoteName);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "webdav", ["vendor"] = "nextcloud", ["url"] = "https://example.test/nextcloud/remote.php/dav/files/user/",
                ["user"] = "user", ["pass"] = PotatoObscured,
                ["pacer_min_sleep"] = "100ms"
            };
            return Task.FromResult<IReadOnlyDictionary<string, string>>(fields);
        }
        public Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>(["cloud", "other", "unselected"]);
        public Task CreateWebDavRemoteAsync(RcloneWebDavRemoteCreateRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task CreateSftpPasswordRemoteAsync(RcloneSftpPasswordRemoteCreateRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task CreateSftpKeyFileRemoteAsync(RcloneSftpKeyFileRemoteCreateRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
