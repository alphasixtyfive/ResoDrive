using System.Diagnostics;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class ApplicationUpdateShutdownTests
{
    [Fact]
    public async Task AcknowledgedShutdownWaitsForTheRetainedHostToExitNaturally()
    {
        using var child = await StartWaitingChildAsync();
        try
        {
            using var host = Open(child);
            var wait = ApplicationUpdateHandoff.WaitForHostShutdownAsync(host, Accepted(child), CancellationToken.None);
            Assert.False(wait.IsCompleted);
            Assert.False(child.HasExited);
            await ExitNaturallyAsync(child);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(host.HasExited);
            Assert.Equal(0, child.ExitCode);
        }
        finally { StopPrivateChild(child); }
    }

    [Fact]
    public async Task AlreadyExitedRetainedHostCompletesWithoutAnotherWait()
    {
        using var child = await StartWaitingChildAsync();
        try
        {
            using var host = Open(child);
            await ExitNaturallyAsync(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var wait = ApplicationUpdateHandoff.WaitForHostShutdownAsync(host, Accepted(child), CancellationToken.None);
            Assert.True(wait.IsCompletedSuccessfully);
            await wait;
        }
        finally { StopPrivateChild(child); }
    }

    [Fact]
    public async Task CancellationLeavesTheRetainedHostRunning()
    {
        using var child = await StartWaitingChildAsync();
        try
        {
            using var host = Open(child);
            using var cancellation = new CancellationTokenSource();
            var wait = ApplicationUpdateHandoff.WaitForHostShutdownAsync(host, Accepted(child), cancellation.Token);
            Assert.False(wait.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.False(child.HasExited);
            Assert.False(host.HasExited);
        }
        finally { StopPrivateChild(child); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task MissingInvalidOrMismatchedShutdownIdentityCannotAuthorizeHandoff(int? processId)
    {
        using var child = await StartWaitingChildAsync();
        try
        {
            using var host = Open(child);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateHandoff.WaitForHostShutdownAsync(
                host, new HostResponse(true, HostProcessId: processId), CancellationToken.None));
            Assert.False(child.HasExited);
        }
        finally { StopPrivateChild(child); }
    }

    [Fact]
    public async Task RejectedShutdownCannotAuthorizeHandoffEvenWithTheSameHostIdentity()
    {
        using var child = await StartWaitingChildAsync();
        try
        {
            using var host = Open(child);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateHandoff.WaitForHostShutdownAsync(
                host, new HostResponse(false, HostProcessId: child.Id), CancellationToken.None));
            Assert.False(child.HasExited);
        }
        finally { StopPrivateChild(child); }
    }

    [Fact]
    public async Task ShutdownAcknowledgementWithoutARetainedHostCannotAuthorizeHandoff()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateHandoff.WaitForHostShutdownAsync(
            null, new HostResponse(true, HostProcessId: Environment.ProcessId), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void SuccessfulUploadCheckRequiresAValidHostIdentity(int? processId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApplicationUpdateHandoff.OpenHostForShutdown(new HostResponse(true, HostProcessId: processId)));
    }

    [Fact]
    public void UnsuccessfulUploadCheckDoesNotOpenAHost()
    {
        Assert.Null(ApplicationUpdateHandoff.OpenHostForShutdown(new HostResponse(false, HostProcessId: Environment.ProcessId)));
    }

    private static Process Open(Process child)
    {
        var host = Assert.IsType<Process>(ApplicationUpdateHandoff.OpenHostForShutdown(Accepted(child)));
        Assert.False(host.SafeHandle.IsInvalid);
        return host;
    }

    private static HostResponse Accepted(Process child) => new(true, HostProcessId: child.Id);

    private static async Task<Process> StartWaitingChildAsync()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "/d", "/q", "/c", "echo ready&set /p ResoDriveTestWait=" })
            start.ArgumentList.Add(argument);
        var child = Process.Start(start) ?? throw new InvalidOperationException("The private shutdown fixture could not start.");
        try
        {
            _ = child.SafeHandle;
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync(readiness.Token));
            Assert.False(child.HasExited);
            return child;
        }
        catch
        {
            StopPrivateChild(child);
            child.Dispose();
            throw;
        }
    }

    private static async Task ExitNaturallyAsync(Process child)
    {
        await child.StandardInput.WriteLineAsync("done");
        await child.StandardInput.FlushAsync();
    }

    private static void StopPrivateChild(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(); // Only this controlled child; its original handle prevents PID reuse.
            Assert.True(child.WaitForExit(5000));
        }
    }
}
