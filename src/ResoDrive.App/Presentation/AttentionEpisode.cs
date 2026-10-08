namespace ResoDrive.App;

internal enum AttentionTransition { Unchanged, Began, Recovered }

/// <summary>Reports an ongoing fault once; only sustained confirmed health rearms it.</summary>
internal sealed class AttentionEpisode(TimeProvider clock)
{
    internal static readonly TimeSpan RecoveryConfirmation = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(30);
    private long? _healthySince;
    private long? _lastHealthyObservation;
    internal bool IsActive { get; private set; }

    internal AttentionTransition Observe(bool attention, bool confirmedHealthy)
    {
        if (attention)
        {
            _healthySince = null;
            _lastHealthyObservation = null;
            if (IsActive) return AttentionTransition.Unchanged;
            IsActive = true;
            return AttentionTransition.Began;
        }
        if (!confirmedHealthy) { _healthySince = null; _lastHealthyObservation = null; return AttentionTransition.Unchanged; }
        if (!IsActive) return AttentionTransition.Unchanged;
        var now = clock.GetTimestamp();
        // Suspension or a stalled status poll is not evidence of continued health.
        if (_lastHealthyObservation is null || clock.GetElapsedTime(_lastHealthyObservation.Value, now) > MaximumObservationGap)
            _healthySince = now;
        _lastHealthyObservation = now;
        _healthySince ??= now;
        if (clock.GetElapsedTime(_healthySince.Value, now) < RecoveryConfirmation)
            return AttentionTransition.Unchanged;
        Reset();
        return AttentionTransition.Recovered;
    }

    internal void Reset() { IsActive = false; _healthySince = null; _lastHealthyObservation = null; }
}
