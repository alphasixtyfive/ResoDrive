namespace ResoDrive.Windows;

// Calls are serialized by the owning mount's operation gate.
internal sealed class MountReadinessProbe(Func<Task<bool>> probe)
{
    private Task<bool>? _pending;

    internal async Task<bool> CheckAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _pending ??= Task.Run(probe, CancellationToken.None);
        try
        {
            return await _pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        finally
        {
            if (_pending.IsCompleted)
                _pending = null;
        }
    }
}
