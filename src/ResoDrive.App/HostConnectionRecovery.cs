using ResoDrive.Windows;

namespace ResoDrive.App;

// Used on the window's dispatcher: suspend before a deliberate host shutdown so
// an older probe cannot mistake it for a crash and launch another host.
internal sealed class HostConnectionRecovery(CancellationToken lifetime) : IDisposable
{
    private CancellationTokenSource _attempts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    public bool IsSuspended { get; private set; }
    public CancellationToken Token => _attempts.Token;

    public void Suspend()
    {
        IsSuspended = true;
        _attempts.Cancel();
    }

    public void Resume()
    {
        if (!IsSuspended || lifetime.IsCancellationRequested) return;
        _attempts.Dispose();
        _attempts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        IsSuspended = false;
    }

    public async Task<HostResponse> EnsureAsync(
        Func<TimeSpan, CancellationToken, Task<HostResponse>> probe,
        Func<HostResponse?> launch)
    {
        var token = _attempts.Token;
        token.ThrowIfCancellationRequested();
        var response = await probe(TimeSpan.FromMilliseconds(250), token);
        token.ThrowIfCancellationRequested();
        if (response.Succeeded ||
            !string.Equals(response.ErrorCode, "host.unavailable", StringComparison.OrdinalIgnoreCase))
            return response;

        var launchError = launch();
        if (launchError is not null) return launchError;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(350, token);
            response = await probe(TimeSpan.FromMilliseconds(750), token);
            token.ThrowIfCancellationRequested();
            if (response.Succeeded) return response;
        }
        return response;
    }

    public void Dispose()
    {
        Suspend();
        _attempts.Dispose();
    }
}
