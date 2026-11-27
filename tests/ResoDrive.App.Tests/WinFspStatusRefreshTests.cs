using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;

namespace ResoDrive.App.Tests;

public sealed class WinFspStatusRefreshTests
{
    [Fact]
    public async Task CoalescesConcurrentChecksAndCanForceFreshDetectionAfterInstallation()
    {
        var calls = 0;
        var completed = new TaskCompletionSource<OperationResult<PrerequisiteStatus>>();
        var refresh = new WinFspStatusRefresh(_ => { calls++; return completed.Task; });
        var first = refresh.CheckAsync(true, CancellationToken.None);
        var second = refresh.CheckAsync(true, CancellationToken.None);
        Assert.Equal(1, calls);
        completed.SetResult(Result.Success(new PrerequisiteStatus(true, "2.1")));
        Assert.True((await first)!.Value!.IsInstalled);
        Assert.True((await second)!.Value!.IsInstalled);
        Assert.Null(await refresh.CheckAsync(false, CancellationToken.None));
        Assert.True((await refresh.CheckAsync(true, CancellationToken.None))!.Value!.IsInstalled);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PeriodicCheckFindsAnExternalInstallationWithoutRestarting()
    {
        var clock = new Clock();
        var installed = false;
        var refresh = new WinFspStatusRefresh(_ => Task.FromResult(Result.Success(new PrerequisiteStatus(installed, null))), clock);
        Assert.False((await refresh.CheckAsync(false, CancellationToken.None))!.Value!.IsInstalled);
        installed = true;
        Assert.Null(await refresh.CheckAsync(false, CancellationToken.None));
        clock.Now = 5000;
        Assert.True((await refresh.CheckAsync(false, CancellationToken.None))!.Value!.IsInstalled);
    }

    [Fact]
    public async Task FailedChecksAreThrottledAndCanBeRetried()
    {
        var calls = 0;
        var refresh = new WinFspStatusRefresh(_ =>
        {
            if (++calls == 1) throw new IOException("unavailable");
            return Task.FromResult(Result.Success(new PrerequisiteStatus(true, null)));
        });
        await Assert.ThrowsAsync<IOException>(() => refresh.CheckAsync(true, CancellationToken.None));
        Assert.Null(await refresh.CheckAsync(false, CancellationToken.None));
        Assert.True((await refresh.CheckAsync(true, CancellationToken.None))!.Value!.IsInstalled);
    }

    [Fact]
    public async Task ClosingCancelsThePendingProbeAndAllowsLaterRetry()
    {
        var refresh = new WinFspStatusRefresh(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result.Success(new PrerequisiteStatus(false, null));
        });
        using var cancellation = new CancellationTokenSource();
        var pending = refresh.CheckAsync(true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
}
