namespace ResoDrive.App.Tests;

public sealed class UploadAttentionNotificationsTests
{
    [Fact]
    public void BriefFailuresAndOrdinaryUploadsNeverSendAnAlert()
    {
        var clock = new ObservationClock();
        var notifications = new UploadAttentionNotifications(clock);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.False(notifications.Observe(true, false, false));
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.False(notifications.Observe(false, false, false));
        }
    }

    [Fact]
    public void SustainedProblemSendsOneWarningAfterThirtySeconds()
    {
        var clock = new ObservationClock();
        var notifications = new UploadAttentionNotifications(clock);
        Assert.False(notifications.Observe(true, false, false));
        for (var poll = 1; poll <= 60; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(poll == 30, notifications.Observe(true, false, false));
        }
        // Brief clean/unknown intervals cannot rearm a previously reported episode.
        Assert.False(notifications.Observe(false, false, true));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(notifications.Observe(true, false, false));
    }

    [Fact]
    public void RecoveryRearmsOnlyAfterSustainedConfirmedHealth()
    {
        var clock = new ObservationClock();
        var notifications = new UploadAttentionNotifications(clock);
        Assert.True(notifications.Observe(true, true, false));
        Assert.False(notifications.Observe(false, false, true));
        for (var poll = 0; poll < 30; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(notifications.Observe(false, false, true));
        }
        Assert.True(notifications.Observe(true, true, false));
    }

    [Fact]
    public void SuspensionDoesNotTurnOneFailedSampleIntoAPersistentProblem()
    {
        var clock = new ObservationClock();
        var notifications = new UploadAttentionNotifications(clock);
        Assert.False(notifications.Observe(true, false, false));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(notifications.Observe(true, false, false));
    }

    [Fact]
    public void RecoveryFailureEscalatesAnAlreadyReportedConnectionProblemOnce()
    {
        var clock = new ObservationClock();
        var notifications = new UploadAttentionNotifications(clock);
        Assert.False(notifications.Observe(true, false, false));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(notifications.Observe(true, false, false));
        Assert.True(notifications.Observe(true, true, false));
        Assert.False(notifications.Observe(true, true, false));
        Assert.False(notifications.Observe(false, false, false));
        Assert.False(notifications.Observe(true, true, false));
    }
}
