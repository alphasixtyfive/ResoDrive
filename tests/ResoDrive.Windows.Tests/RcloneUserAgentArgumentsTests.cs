namespace ResoDrive.Windows.Tests;

public sealed class RcloneUserAgentArgumentsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Default_PassesWholeClientIdentityAsOneArgument(string? inheritedUserAgent)
    {
        var arguments = RcloneUserAgentArguments.Create(["--timeout", "30s"], inheritedUserAgent);

        Assert.Equal(["--user-agent", ClientUserAgent.Value], arguments);
        Assert.StartsWith("ResoDrive/", arguments[1]);
    }

    [Theory]
    [InlineData("ManagedClient/1.2")]
    [InlineData(" ")]
    public void ExplicitEnvironmentOverride_IsPreserved(string inheritedUserAgent) =>
        Assert.Empty(RcloneUserAgentArguments.Create([], inheritedUserAgent));

    [Fact]
    public void ExplicitOptionOverride_IsPreserved() =>
        Assert.Empty(RcloneUserAgentArguments.Create(["--user-agent", "ManagedClient/1.2"], null));

    [Theory]
    [InlineData("--user-agent=ManagedClient/1.2")]
    [InlineData("--user-agent=")]
    [InlineData("--USER-AGENT=ManagedClient/1.2")]
    public void ExplicitInlineOverride_IsPreserved(string option) =>
        Assert.Empty(RcloneUserAgentArguments.Create([option], null));

    [Fact]
    public void OtherBackendUserAgentOption_DoesNotSuppressDefault() =>
        Assert.Equal(["--user-agent", ClientUserAgent.Value],
            RcloneUserAgentArguments.Create(["--pikpak-user-agent=ProviderSpecific/1.0"], null));
}
