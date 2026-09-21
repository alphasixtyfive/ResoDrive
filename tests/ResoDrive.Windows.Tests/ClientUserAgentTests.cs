using System.Reflection;
using System.Runtime.InteropServices;

namespace ResoDrive.Windows.Tests;

public sealed class ClientUserAgentTests
{
    [Fact]
    public void Windows11HeaderIncludesPatchRevisionAndBothArchitecturesWithoutCommitMetadata()
    {
        var value = ClientUserAgent.Format("0.3.8+commit-hash", new Version(10, 0, 26100, 0),
            "Client", "Professional", "24H2", 9457, Architecture.X64, Architecture.Arm64);

        Assert.Equal("ResoDrive/0.3.8 (Windows 11; Edition: Professional; Release: 24H2; " +
            "Build: 10.0.26100.9457; ClientArchitecture: x64; OSArchitecture: arm64)", value);
        AssertValidHeader(value);
    }

    [Theory]
    [InlineData("Client", 19044, "EnterpriseS", "Windows 10")]
    [InlineData("Client", 26100, "IoTEnterpriseS", "Windows 11")]
    [InlineData("Server", 26100, "ServerStandard", "Windows Server")]
    [InlineData("Server Core", 26100, "ServerDatacenter", "Windows Server")]
    public void EditionAndInstallationTypeDistinguishLtscAndServer(string installationType, int build,
        string edition, string platform)
    {
        var value = ClientUserAgent.Format("0.3.8", new Version(10, 0, build),
            installationType, edition, null, null, Architecture.X64, Architecture.X64);

        Assert.StartsWith($"ResoDrive/0.3.8 ({platform}; Edition: {edition};", value, StringComparison.Ordinal);
        AssertValidHeader(value);
    }

    [Fact]
    public void MissingOptionalMetadataDoesNotGuessWindowsEditionOrPatchRevision()
    {
        var value = ClientUserAgent.Format("0.3.8", new Version(10, 0, 26100, 0),
            null, null, null, null, Architecture.X64, Architecture.X64);

        Assert.Equal("ResoDrive/0.3.8 (Windows; Build: 10.0.26100; ClientArchitecture: x64; OSArchitecture: x64)", value);
        AssertValidHeader(value);
    }

    [Theory]
    [InlineData("Pro\r\nInjected: true")]
    [InlineData("Pro) ExtraProduct/1.0 (")]
    [InlineData("Pro\\Bad")]
    [InlineData("Pro; invalid")]
    [InlineData("é")]
    [InlineData(" ")]
    public void InvalidMetadataCannotIntroduceExtraHeaderContent(string metadata)
    {
        var value = ClientUserAgent.Format(metadata, new Version(10, 0, 26100),
            metadata, metadata, metadata, -1, Architecture.X64, Architecture.X64);

        Assert.Equal("ResoDrive/unknown (Windows; Build: 10.0.26100; ClientArchitecture: x64; OSArchitecture: x64)", value);
        AssertValidHeader(value);
    }

    [Fact]
    public void OversizedMetadataIsOmitted()
    {
        var metadata = new string('x', 65);
        var value = ClientUserAgent.Format("0.3.8", new Version(10, 0, 26100),
            null, metadata, metadata, null, Architecture.X64, Architecture.X64);

        Assert.DoesNotContain(metadata, value, StringComparison.Ordinal);
        AssertValidHeader(value);
    }

    [Fact]
    public void RuntimeHeaderUsesTheProductAssemblyVersion()
    {
        using var request = new HttpRequestMessage();
        request.Headers.UserAgent.ParseAdd(ClientUserAgent.Value);
        var product = request.Headers.UserAgent.First().Product!;
        var version = typeof(ClientUserAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];

        Assert.Equal("ResoDrive", product.Name);
        Assert.Equal(version, product.Version);
        AssertValidHeader(ClientUserAgent.Value);
    }

    private static void AssertValidHeader(string value)
    {
        using var request = new HttpRequestMessage();
        request.Headers.UserAgent.ParseAdd(value);
        Assert.Equal(2, request.Headers.UserAgent.Count);
        Assert.Equal(value, request.Headers.UserAgent.ToString());
        Assert.True(value.Length < 512);
    }
}
