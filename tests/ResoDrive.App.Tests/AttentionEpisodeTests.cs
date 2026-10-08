namespace ResoDrive.App.Tests;

internal sealed class ObservationClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
}

public sealed class AttentionEpisodeTests
{
    [Fact]
    public void OngoingWarningAndBriefClearSamplesDoNotProducePeriodicAlerts()
    {
        var clock = new ObservationClock();
        var episode = new AttentionEpisode(clock);
        Assert.Equal(AttentionTransition.Began, episode.Observe(true, false));
        for (var poll = 0; poll < 100; poll++)
        {
            Assert.Equal(AttentionTransition.Unchanged, episode.Observe(false, true));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(AttentionTransition.Unchanged, episode.Observe(true, false));
        }
        Assert.True(episode.IsActive);
        Assert.Equal(AttentionTransition.Unchanged, episode.Observe(false, true));
        ConfirmRecovery(episode, clock);
        Assert.Equal(AttentionTransition.Began, episode.Observe(true, false));
    }

    [Fact]
    public void UnknownStatusInterruptsTheHealthyObservationPeriod()
    {
        var clock = new ObservationClock();
        var episode = new AttentionEpisode(clock);
        _ = episode.Observe(true, false);
        _ = episode.Observe(false, true);
        clock.Advance(TimeSpan.FromMinutes(4));
        _ = episode.Observe(false, false);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AttentionTransition.Unchanged, episode.Observe(false, true));
        Assert.True(episode.IsActive);
        ConfirmRecovery(episode, clock);
    }

    [Fact]
    public void SuspensionOrStalledPollingCannotCountAsFiveHealthyMinutes()
    {
        var clock = new ObservationClock();
        var episode = new AttentionEpisode(clock);
        _ = episode.Observe(true, false);
        _ = episode.Observe(false, true);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AttentionTransition.Unchanged, episode.Observe(false, true));
        Assert.True(episode.IsActive);
        ConfirmRecovery(episode, clock);
    }

    private static void ConfirmRecovery(AttentionEpisode episode, ObservationClock clock)
    {
        for (var poll = 1; poll <= 30; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(poll == 30 ? AttentionTransition.Recovered : AttentionTransition.Unchanged,
                episode.Observe(false, true));
        }
    }
}
