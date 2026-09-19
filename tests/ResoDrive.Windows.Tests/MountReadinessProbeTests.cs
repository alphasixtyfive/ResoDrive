using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class MountReadinessProbeTests
{
    [Fact]
    public async Task SlowFilesystemKeepsOnlyOneProbeAndCanRecover()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new MountReadinessProbe(() => { Interlocked.Increment(ref calls); return completion.Task; });
        Assert.False(await probe.CheckAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None));
        Assert.False(await probe.CheckAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None));
        Assert.Equal(1, calls);
        completion.SetResult(true);
        Assert.True(await probe.CheckAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(await probe.CheckAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAnUnavailableDrive()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new MountReadinessProbe(() => completion.Task);
        using var cancellation = new CancellationTokenSource();
        var check = probe.CheckAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        completion.SetResult(true);
        Assert.True(await probe.CheckAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
