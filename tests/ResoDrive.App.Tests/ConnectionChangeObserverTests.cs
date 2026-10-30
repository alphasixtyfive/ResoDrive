using ResoDrive.Host;

namespace ResoDrive.App.Tests;

public sealed class ConnectionChangeObserverTests
{
    [Fact]
    public void FlappingNetworkSettlesBeforeOneRetryHintIsConsumed()
    {
        var clock = new ObservationClock();
        using var changes = new ConnectionChangeObserver(clock, listen: false);
        Assert.False(changes.Consume());
        for (var change = 0; change < 20; change++)
        {
            changes.NotifyChanged();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(changes.Consume());
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(changes.Consume());
        Assert.False(changes.Consume());
    }

    [Fact]
    public void ResumeRetriesAfterSettlingWithoutRequiringAnAddressEvent()
    {
        var clock = new ObservationClock();
        using var changes = new ConnectionChangeObserver(clock, listen: false);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(changes.Consume());
        clock.Advance(ConnectionChangeObserver.SettleDelay);
        Assert.True(changes.Consume());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(changes.Consume());
    }

    [Fact]
    public void DisposedObserverCannotScheduleMoreWorkFromLateCallbacks()
    {
        var clock = new ObservationClock();
        var changes = new ConnectionChangeObserver(clock, listen: false);
        changes.NotifyChanged();
        changes.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        changes.NotifyChanged();
        Assert.False(changes.Consume());
    }
}
