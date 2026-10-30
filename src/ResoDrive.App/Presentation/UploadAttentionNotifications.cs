namespace ResoDrive.App;

/// <summary>Keeps brief connection failures quiet and reports a sustained episode once.</summary>
internal sealed class UploadAttentionNotifications(TimeProvider clock)
{
    internal static readonly TimeSpan WarningDelay = TimeSpan.FromSeconds(30);
    private readonly AttentionEpisode _episode = new(clock);
    private long? _warningSince;
    private long? _lastWarning;
    private bool _urgentReported;

    internal bool Observe(bool warning, bool immediate, bool confirmedHealthy)
    {
        if (!warning)
        {
            _warningSince = _lastWarning = null;
            if (_episode.Observe(false, confirmedHealthy) == AttentionTransition.Recovered)
                _urgentReported = false;
            return false;
        }
        var now = clock.GetTimestamp();
        if (_lastWarning is null || clock.GetElapsedTime(_lastWarning.Value, now) > WarningDelay)
            _warningSince = now;
        _lastWarning = now;
        _warningSince ??= now;
        // Recovery/power failures are a material escalation from an ordinary retry.
        if (immediate && !_urgentReported)
        {
            _urgentReported = true;
            _episode.Observe(true, false);
            return true;
        }
        if (!immediate && clock.GetElapsedTime(_warningSince.Value, now) < WarningDelay)
        {
            _episode.Observe(false, false);
            return false;
        }
        return _episode.Observe(true, false) == AttentionTransition.Began;
    }
}
