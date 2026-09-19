using System.Diagnostics;

namespace ResoDrive.Windows.Tests;

public sealed class RemoteWipeBoundaryTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "resodrive-wipe-boundary", Guid.NewGuid().ToString("N")));

    public RemoteWipeBoundaryTests() => _paths.EnsureCreated();

    [Fact]
    public async Task StopsVerifiedOwnedProcessAndLeavesUnrelatedProcessRunning()
    {
        using var owned = StartSleeper();
        using var unrelated = StartSleeper();
        try
        {
            using var ownership = new MountOwnershipStore(_paths);
            await ownership.UpsertAsync(new(Guid.NewGuid(), owned.Id, owned.StartTime.ToUniversalTime(),
                owned.MainModule!.FileName!, "fixture:", "Z:"), CancellationToken.None);
            // A reused/mismatched PID must not grant authority to terminate another process.
            await ownership.UpsertAsync(new(Guid.NewGuid(), unrelated.Id, unrelated.StartTime.ToUniversalTime().AddMinutes(-1),
                unrelated.MainModule!.FileName!, "fixture:", "Y:"), CancellationToken.None);
            await RemoteWipeWorkStopper.StopOwnedMountsAsync(_paths, CancellationToken.None);
            Assert.True(owned.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync(); }
            if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task UnreadableOwnershipCannotBeTreatedAsNoRunningMounts()
    {
        File.WriteAllText(_paths.OwnershipFile, "invalid");
        await Assert.ThrowsAsync<IOException>(() => RemoteWipeWorkStopper.StopOwnedMountsAsync(_paths, CancellationToken.None));
    }

    [Fact]
    public async Task CleanupRefusesCacheJunctionWithoutTouchingExternalFiles()
    {
        var outside = _paths.Root + "-outside";
        var junction = Path.Combine(_paths.Cache, "redirected");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside managed cache");
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Throws<IOException>(() => RemoteWipeCleanup.DeleteAccountData(_paths));
            Assert.Equal("outside managed cache", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(outside, true);
        }
    }

    private static Process StartSleeper()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
