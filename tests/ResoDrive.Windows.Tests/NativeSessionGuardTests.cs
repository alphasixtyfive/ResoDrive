using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ResoDrive.Windows.Hosting;

namespace ResoDrive.Windows.Tests;

public sealed class NativeSessionGuardTests
{
    [Fact]
    public async Task UnknownStartupAndPendingWorkRejectOnlyAnIsolatedWindowQuery()
    {
        await using var guard = new NativeSessionGuard();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = await guard.WaitUntilReadyAsync(timeout.Token);
        Assert.True(ready.WindowReady, ready.Error);
        Assert.True(ready.ShutdownReasonRegistered, ready.Error);
        Assert.True(ready.AutomaticSleepPrevented, ready.Error);
        Assert.True(guard.BlocksSessionEnding);
        Assert.False(QueryOnlyThisWindow(guard.WindowHandle));

        guard.Update(true, "ResoDrive is uploading 2 files. Wait for the upload to finish.");
        var pending = await guard.SynchronizeAsync(timeout.Token);
        Assert.Null(pending.Error);
        Assert.Contains("uploading 2 files", ReadShutdownReason(guard.WindowHandle), StringComparison.Ordinal);
        Assert.False(QueryOnlyThisWindow(guard.WindowHandle));

        // WM_ENDSESSION(FALSE) means another application or the user canceled shutdown.
        SendToOnlyThisWindow(guard.WindowHandle, 0x0016, 0, 0);
        Assert.False(QueryOnlyThisWindow(guard.WindowHandle));

        guard.Update(false);
        var safe = await guard.SynchronizeAsync(timeout.Token);
        Assert.Null(safe.Error);
        Assert.False(safe.ShutdownReasonRegistered);
        Assert.False(safe.AutomaticSleepPrevented);
        Assert.True(QueryOnlyThisWindow(guard.WindowHandle));
        Assert.Null(ReadShutdownReason(guard.WindowHandle));
    }

    [Fact]
    public async Task LatestSnapshotControlsQueryEvenBeforeThePostedRefreshIsConsumed()
    {
        await using var guard = new NativeSessionGuard();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await guard.WaitUntilReadyAsync(timeout.Token);
        guard.Update(false);
        Assert.True(QueryOnlyThisWindow(guard.WindowHandle));
        guard.Update(true, "ResoDrive cannot confirm that all files have uploaded.");
        Assert.False(QueryOnlyThisWindow(guard.WindowHandle));
    }

    [Theory]
    [InlineData(0x80000000L)] // ENDSESSION_LOGOFF
    [InlineData(0x40000000L)] // ENDSESSION_CRITICAL; Windows can still override the answer.
    public async Task LogoffAndCriticalQueriesKeepPendingWorkProtected(long sessionFlags)
    {
        await using var guard = new NativeSessionGuard();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await guard.WaitUntilReadyAsync(timeout.Token);
        Assert.Equal(0UL, (ulong)SendToOnlyThisWindow(guard.WindowHandle, 0x0011, 0, (nint)sessionFlags));
        SendToOnlyThisWindow(guard.WindowHandle, 0x0016, 1, (nint)sessionFlags);
        Assert.True(guard.BlocksSessionEnding);
        Assert.True(IsWindow(guard.WindowHandle));
    }

    [Fact]
    public async Task DisposalDuringStartupCompletesAndRejectsLaterUpdates()
    {
        var guard = new NativeSessionGuard();
        await guard.DisposeAsync();
        await guard.DisposeAsync();
        guard.Dispose();
        Assert.Equal(0, guard.WindowHandle);
        Assert.False(guard.Status.WindowReady);
        Assert.False(guard.Status.ShutdownReasonRegistered);
        Assert.False(guard.Status.AutomaticSleepPrevented);
        Assert.Throws<ObjectDisposedException>(() => guard.Update(true));
    }

    [Fact]
    public async Task DisposedWindowIsDestroyedAndGuardInstancesRemainIndependent()
    {
        await using var safeGuard = new NativeSessionGuard();
        var pendingGuard = new NativeSessionGuard();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(safeGuard.WaitUntilReadyAsync(timeout.Token), pendingGuard.WaitUntilReadyAsync(timeout.Token));
        safeGuard.Update(false);
        Assert.True(QueryOnlyThisWindow(safeGuard.WindowHandle));
        Assert.False(QueryOnlyThisWindow(pendingGuard.WindowHandle));
        var pendingWindow = pendingGuard.WindowHandle;
        await pendingGuard.DisposeAsync();
        Assert.False(IsWindow(pendingWindow));
        Assert.True(QueryOnlyThisWindow(safeGuard.WindowHandle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\0\t")]
    public void EmptyShutdownReasonHasAFactualFallback(string? reason)
    {
        Assert.Equal("ResoDrive is checking for files that still need uploading.",
            NativeSessionGuard.NormalizeReason(reason));
    }

    [Fact]
    public void ShutdownReasonIsBoundedAndDoesNotSplitASurrogatePair()
    {
        var reason = NativeSessionGuard.NormalizeReason(new string('a', 239) + "\U0001F4C1" + "more");
        Assert.Equal(239, reason.Length);
        Assert.DoesNotContain('\r', NativeSessionGuard.NormalizeReason("Files\r\nneed\0uploading."));
    }

    private static bool QueryOnlyThisWindow(nint window) =>
        SendToOnlyThisWindow(window, 0x0011, 0, 0) != 0;

    private static nuint SendToOnlyThisWindow(nint window, uint message, nuint parameter, nint details)
    {
        Assert.NotEqual(0, window);
        var sent = SendMessageTimeout(window, message, parameter, details, 0x0002, 5000, out var result);
        Assert.NotEqual(0, sent);
        return result;
    }

    private static string? ReadShutdownReason(nint window)
    {
        uint length = 256;
        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            return ShutdownBlockReasonQuery(window, buffer, ref length) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "This test project does not enable unsafe source-generated interop.")]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint parameter, nint details,
        uint flags, uint timeout, out nuint result);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "This test project does not enable unsafe source-generated interop.")]
    private static extern bool ShutdownBlockReasonQuery(nint window, nint buffer, ref uint length);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "This test project does not enable unsafe source-generated interop.")]
    private static extern bool IsWindow(nint window);
}
