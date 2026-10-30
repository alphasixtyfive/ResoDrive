using ResoDrive.Core.Setup;

namespace ResoDrive.Core.Tests;

public sealed class SetupProfileSchemaV2Tests
{
    private const string PublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAINdamAGCsQq31Uv+08lkBzoO4XLz2qYjJa8CGmj3B1Ea";

    [Fact]
    public void ParseV2_CreatesTypedWebDavProfileWithoutUiMetadata()
    {
        var result = SetupProfileCatalogJson.Parse(WebDavJson);

        Assert.True(result.Succeeded, result.Error?.Message);
        var profile = Assert.Single(result.Value!);
        var connection = Assert.IsType<WebDavConnectionDefinition>(profile.Connection);
        Assert.Equal(new Uri("https://storage.example.org/"), connection.BaseUrl);
        Assert.Equal(WebDavVendor.Nextcloud, connection.Vendor);
    }

    [Fact]
    public void ParseV2_CreatesPasswordSftpProfileWithPinnedHostKey()
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson());

        Assert.True(result.Succeeded, result.Error?.Message);
        var profile = Assert.Single(result.Value!);
        var connection = Assert.IsType<SftpConnectionDefinition>(profile.Connection);
        Assert.Equal("files.example.org", connection.Host);
        Assert.Equal(2222, connection.Port);
        Assert.Equal($"ssh-ed25519 {PublicKey}", connection.KnownHost);
        Assert.Equal(SftpAuthenticationMethod.Password, connection.Authentication);
    }

    [Fact]
    public void ParseV2_CreatesPrivateKeySftpProfile()
    {
        var result = SetupProfileCatalogJson.Parse(
            SftpJson().Replace("\"sftpPassword\"", "\"sftpKeyFile\"", StringComparison.Ordinal));

        Assert.True(result.Succeeded, result.Error?.Message);
        var connection = Assert.IsType<SftpConnectionDefinition>(Assert.Single(result.Value!).Connection);
        Assert.Equal(SftpAuthenticationMethod.PrivateKey, connection.Authentication);
    }

    [Fact]
    public void ParseV2_RejectsEarlierSchemaVersions()
    {
        var result = SetupProfileCatalogJson.Parse(
            WebDavJson.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal));

        Assert.False(result.Succeeded);
        Assert.Equal("profiles.schema", result.Error?.Code);
    }

    [Theory]
    [InlineData("ftp")]
    [InlineData("SFTPPassword")]
    [InlineData("webDav")]
    public void ParseV2_RejectsUnknownOrIncorrectlyCasedConnectionType(string type)
    {
        var result = SetupProfileCatalogJson.Parse(
            WebDavJson.Replace("\"webdav\"", $"\"{type}\"", StringComparison.Ordinal));

        Assert.False(result.Succeeded);
        Assert.Equal("profiles.invalid_json", result.Error?.Code);
    }

    [Theory]
    [InlineData("bad host")]
    [InlineData("user@files.example.org")]
    [InlineData("files.example.org:22")]
    [InlineData("https://files.example.org")]
    public void ParseV2_RejectsUnsafeSftpHosts(string host)
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(host: host));

        Assert.False(result.Succeeded);
        Assert.Equal("profile.sftp.host", result.Error?.Code);
    }

    [Fact]
    public void ParseV2_AcceptsUnbracketedIpv6SftpHost()
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(host: "2001:db8::1"));

        Assert.True(result.Succeeded, result.Error?.Message);
        var connection = Assert.IsType<SftpConnectionDefinition>(Assert.Single(result.Value!).Connection);
        Assert.Equal("2001:db8::1", connection.Host);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseV2_AllowsOptionalSftpHostKey(bool omitProperty)
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(
            knownHost: string.Empty,
            omitKnownHost: omitProperty));

        Assert.True(result.Succeeded, result.Error?.Message);
        var connection = Assert.IsType<SftpConnectionDefinition>(Assert.Single(result.Value!).Connection);
        Assert.Empty(connection.KnownHost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ParseV2_RejectsInvalidSftpPorts(int port)
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(port: port));

        Assert.False(result.Succeeded);
        Assert.Equal("profile.sftp.port", result.Error?.Code);
    }

    [Theory]
    [InlineData("ssh-ed25519 not-base64")]
    [InlineData("ssh-dss AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("files.example.org ssh-ed25519 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void ParseV2_RejectsMalformedSftpHostKeys(string knownHost)
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(knownHost: knownHost));

        Assert.False(result.Succeeded);
        Assert.True(result.Error?.Code is "profiles.invalid_json" or "profile.sftp.knownHost");
    }

    [Fact]
    public void ParseV2_RejectsAnAlgorithmThatDoesNotMatchTheEncodedKey()
    {
        var result = SetupProfileCatalogJson.Parse(SftpJson(knownHost: $"ssh-rsa {PublicKey}"));
        Assert.False(result.Succeeded);
        Assert.Equal("profile.sftp.knownHost", result.Error?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ParseV2_RejectsDeprecatedInputMetadata()
    {
        var result = SetupProfileCatalogJson.Parse(WithDeprecatedInputs(WebDavJson));

        Assert.False(result.Succeeded);
        Assert.Equal("profiles.invalid_json", result.Error?.Code);
    }

    [Fact]
    public void ParseV2_RejectsUnknownConnectionProperties()
    {
        var result = SetupProfileCatalogJson.Parse(
            WebDavJson.Replace("\"vendor\": \"nextcloud\"", "\"vendor\": \"nextcloud\", \"command\": \"calc.exe\"", StringComparison.Ordinal));

        Assert.False(result.Succeeded);
        Assert.Equal("profiles.invalid_json", result.Error?.Code);
    }

    [Fact]
    public void ParseV2_AllowsStaticPathForGenericWebDavButNotNextcloud()
    {
        var generic = WebDavJson
            .Replace("\"vendor\": \"nextcloud\"", "\"vendor\": \"other\"", StringComparison.Ordinal)
            .Replace("/dav/{username}", "/dav", StringComparison.Ordinal);
        var nextcloud = WebDavJson.Replace("/dav/{username}", "/dav", StringComparison.Ordinal);

        Assert.True(SetupProfileCatalogJson.Parse(generic).Succeeded);
        Assert.Equal("profile.webDavPath", SetupProfileCatalogJson.Parse(nextcloud).Error?.Code);
    }

    [Fact]
    public void ParseV2_RejectsUnknownWebDavTemplatePlaceholders()
    {
        var result = SetupProfileCatalogJson.Parse(
            WebDavJson.Replace("/dav/{username}", "/dav/{username}/{password}", StringComparison.Ordinal));

        Assert.False(result.Succeeded);
        Assert.Equal("profile.webDavPath", result.Error?.Code);
    }

    private static string SftpJson(
        string host = "files.example.org",
        int port = 2222,
        string? knownHost = null,
        bool omitKnownHost = false)
    {
        var knownHostProperty = omitKnownHost
            ? string.Empty
            : $",\n              \"knownHost\": \"{knownHost ?? $"ssh-ed25519 {PublicKey}"}\"";
        return $$"""
        {
          "schemaVersion": 2,
          "profiles": [{
            "id": "sftp",
            "displayName": "SFTP",
            "description": "Password protected SSH storage",
            "defaultRemoteName": "Server",
            "connection": {
              "type": "sftpPassword",
              "host": "{{host}}",
              "port": {{port}}{{knownHostProperty}}
            },
            "defaultDriveLetter": "S",
            "mountArguments": []
          }]
        }
        """;
    }

    private static string WithDeprecatedInputs(string json) => json.Replace(
        "\"defaultDriveLetter\"",
        """
        "inputs": [
          { "key": "username", "kind": "text", "label": "Username" },
          { "key": "password", "kind": "password", "label": "App password" }
        ],
        "defaultDriveLetter"
        """,
        StringComparison.Ordinal);

    private const string WebDavJson = """
        {
          "schemaVersion": 2,
          "profiles": [{
            "id": "cloud",
            "displayName": "Cloud",
            "description": "Nextcloud storage",
            "defaultRemoteName": "Storage",
            "connection": {
              "type": "webdav",
              "baseUrl": "https://storage.example.org/",
              "pathTemplate": "/dav/{username}",
              "vendor": "nextcloud"
            },
            "defaultDriveLetter": "V",
            "mountArguments": []
          }]
        }
        """;

}
