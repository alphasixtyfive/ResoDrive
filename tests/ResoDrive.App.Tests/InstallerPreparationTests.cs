namespace ResoDrive.App.Tests;

public sealed class InstallerPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulPreparationLetsInstallerContinueWithoutAnotherWindow(bool interactive)
    {
        var called = false;
        var exitCode = InstallerPreparation.Run(interactive, () =>
        {
            called = true;
            return Task.FromResult(new InstallerPreparationResult(true, "Ready.", DateTimeOffset.UtcNow));
        }, _ => Assert.Fail("Routine preparation must not open another window."));

        Assert.True(called);
        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedPreparationStopsInstallationAndRetainsItsExactReason(bool interactive)
    {
        const string reason = "Cached uploads need attention. Open Transfers before retrying. Error ID: test1234";
        var prepared = false;
        string? displayed = null;
        var exitCode = InstallerPreparation.Run(interactive, () =>
        {
            prepared = true;
            return Task.FromResult(new InstallerPreparationResult(false, reason, DateTimeOffset.UtcNow));
        }, message =>
        {
            Assert.True(prepared);
            displayed = message;
        });

        Assert.Equal(1, exitCode);
        Assert.Equal(interactive ? reason : null, displayed);
    }
}
