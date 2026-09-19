namespace ResoDrive.Windows;

/// <summary>Durable cleanup/acknowledgement state machine. Never acknowledges partial cleanup.</summary>
public sealed class RemoteWipeCoordinator(ApplicationPaths paths, RemoteWipeClient client)
{
    private readonly RemoteWipeStateStore _state = new(paths);

    public async Task AcceptAsync(RemoteWipeRegistration registration, CancellationToken cancellationToken)
    {
        using var lease = await AccountDataGuard.LockAsync(paths, cancellationToken).ConfigureAwait(false);
        if (_state.Read() is { Phase: not RemoteWipePhase.Completed }) return;
        await _state.SaveAsync(new(Guid.NewGuid(), RemoteWipePhase.Requested, registration), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResumeAsync(Func<CancellationToken, Task> stopWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopWork);
        var state = _state.Read();
        if (state is null or { Phase: RemoteWipePhase.Completed }) return;
        if (state.Phase == RemoteWipePhase.Requested)
        {
            await stopWork(cancellationToken).ConfigureAwait(false);
            using var lease = await AccountDataGuard.LockAsync(paths, cancellationToken).ConfigureAwait(false);
            RemoteWipeCleanup.DeleteAccountData(paths);
            // Only the one revoked token is retained for acknowledgement; other registrations are secrets too.
            File.Delete(paths.RemoteWipeFile);
            state = state with { Phase = RemoteWipePhase.Cleaned };
            await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        var acknowledged = await client.SignalSuccessAsync(state.Registration!, cancellationToken).ConfigureAwait(false);
        using var completedLease = await AccountDataGuard.LockAsync(paths, cancellationToken).ConfigureAwait(false);
        // Keep a token-free generation marker so a still-open UI cannot restore old account state.
        await _state.SaveAsync(state with { Phase = RemoteWipePhase.Completed, Registration = null,
            ServerAcknowledged = acknowledged }, cancellationToken)
            .ConfigureAwait(false);
    }
}
