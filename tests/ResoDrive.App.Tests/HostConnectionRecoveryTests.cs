using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class HostConnectionRecoveryTests
{
    [Fact]
    public async Task DelayedUnavailableProbeCannotLaunchHostAfterShutdownBegins()
    {
        using var recovery = new HostConnectionRecovery(CancellationToken.None);
        var probe = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var connecting = recovery.EnsureAsync((_, _) => probe.Task, () => { launches++; return null; });

        recovery.Suspend();
        probe.SetResult(new(false, "host.unavailable", "Host unavailable"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task CancelledExitCanReconnectWithoutRevivingItsOlderProbe()
    {
        using var recovery = new HostConnectionRecovery(CancellationToken.None);
        var probe = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var oldAttempt = recovery.EnsureAsync((_, _) => probe.Task, () => { launches++; return null; });
        recovery.Suspend();
        recovery.Resume();
        probe.SetResult(new(false, "host.unavailable", "Host unavailable"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldAttempt);
        var fresh = await recovery.EnsureAsync((_, _) => Task.FromResult(new HostResponse(true)),
            () => { launches++; return null; });
        Assert.True(fresh.Succeeded);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task ClosingWindowCancelsStartupRetryBeforeAnotherProbe()
    {
        using var lifetime = new CancellationTokenSource();
        using var recovery = new HostConnectionRecovery(lifetime.Token);
        var probes = 0;
        var connecting = recovery.EnsureAsync((_, _) =>
        {
            probes++;
            return Task.FromResult(new HostResponse(false, "host.unavailable", "Host unavailable"));
        }, () => { lifetime.Cancel(); return null; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(1, probes);
    }
}
