namespace ResoDrive.Windows.Tests;

public sealed class RcloneConnectionMetadataServiceTests
{
    [Fact]
    public void Parse_ReadsWebDavAndSftpWithoutCredentials()
    {
        const string redacted = """
            [Cloud]
            type = webdav
            url = https://cloud.example.com/remote.php/dav/files/user
            user = XXX
            pass = XXX
            [Server]
            type = sftp
            host = files.example.net
            user = XXX
            pass = XXX
            """;

        var connections = RcloneConnectionMetadataService.Parse(redacted);

        Assert.Equal(new RcloneConnectionMetadata("cloud.example.com", "WebDAV")
            { Address = "https://cloud.example.com:443" }, connections["Cloud"]);
        Assert.Equal(new RcloneConnectionMetadata("files.example.net", "SFTP")
            { Address = "sftp://files.example.net:22" }, connections["Server"]);
        Assert.Equal(2, connections.Count);
    }

    [Fact]
    public void Parse_IgnoresMalformedEndpoints()
    {
        const string redacted = """
            [Broken]
            url = not a URL
            host = not a host
            """;

        Assert.Empty(RcloneConnectionMetadataService.Parse(redacted));
    }

    [Theory]
    [InlineData("https://user:password@cloud.example.com:8443/account/path?token=secret#private", "https://cloud.example.com:8443")]
    [InlineData("http://cloud.example.com/folder", "http://cloud.example.com:80")]
    [InlineData("https://[2001:db8::1]:8443/folder", "https://[2001:db8::1]:8443")]
    public void Parse_WebDavAddressIncludesPortWithoutCredentialsOrAccountPaths(string url, string expected)
    {
        var connections = RcloneConnectionMetadataService.Parse($"[Cloud]\ntype = webdav\nurl = {url}");
        Assert.Equal(expected, connections["Cloud"].Address);
    }

    [Theory]
    [InlineData("files.example.net", "2222", "sftp://files.example.net:2222")]
    [InlineData("2001:db8::1", "22", "sftp://[2001:db8::1]:22")]
    [InlineData("[2001:db8::1]", "2222", "sftp://[2001:db8::1]:2222")]
    [InlineData("files.example.net", "invalid", null)]
    [InlineData("files.example.net", "0", null)]
    [InlineData("files.example.net", "65536", null)]
    public void Parse_SftpAddressUsesConfiguredPortAndBracketsIpv6(string host, string port, string? expected)
    {
        var connections = RcloneConnectionMetadataService.Parse($"[Server]\nport = {port}\nhost = {host}\ntype = sftp");
        Assert.Equal(expected, connections["Server"].Address);
    }

    [Fact]
    public void Parse_DoesNotCarryAnAddressOrPortIntoTheNextConnection()
    {
        var connections = RcloneConnectionMetadataService.Parse("""
            [Custom]
            type = sftp
            host = files.example.net
            port = 2222
            [Default]
            type = sftp
            host = other.example.net
            [Unknown]
            type = webdav
            """);
        Assert.Equal("sftp://other.example.net:22", connections["Default"].Address);
        Assert.Null(connections["Unknown"].Address);
    }
}
