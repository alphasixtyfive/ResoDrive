using System.Diagnostics;
using System.Security.Cryptography;

namespace ResoDrive.Windows.Tests;

[Collection(RcloneRuntimeMutationTestGroup.Name)]
public sealed class RcloneLiveUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicUpdatePreservesRunningImageOwnershipAndCacheIncludingRollback(bool rejectInstalled)
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "resodrive-live-update-tests", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var oldImage = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var newImage = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        File.Copy(oldImage, paths.RcloneExecutable);
        var cached = Path.Combine(paths.Cache, "pending-disposable-file");
        await File.WriteAllTextAsync(cached, "Pending upload must survive runtime replacement.");
        var cacheHash = Hash(cached);
        using var process = Process.Start(new ProcessStartInfo(paths.RcloneExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "127.0.0.1", "-n", "120" },
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (process.MainModule?.FileName is null) await Task.Delay(10, deadline.Token);
            var owned = new OwnedMount(Guid.NewGuid(), process.Id, process.StartTime.ToUniversalTime(),
                paths.RcloneExecutable, "fixture:", "R:");
            var runner = new ImageRunner(newImage, rejectInstalled);
            var locator = new RcloneRuntimeLocator(paths, runner);
            var service = new RcloneUpdateService(locator, runner, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            var result = await service.UpdateAsync(cancellationToken: deadline.Token);

            Assert.Equal(!rejectInstalled, result.Succeeded);
            Assert.False(process.HasExited);
            Assert.Equal(cacheHash, Hash(cached));
            using var verified = MountOwnershipStore.TryOpenVerified(owned);
            Assert.NotNull(verified);
            Assert.Equal(process.Id, verified.Id);
            Assert.Equal(Hash(rejectInstalled ? oldImage : newImage), Hash(paths.RcloneExecutable));
            if (rejectInstalled)
                Assert.Equal("rclone.update_rolled_back", result.Error!.Code);
            else
            {
                Assert.NotNull(result.Value!.CleanupPath);
                Assert.Equal(Hash(oldImage), Hash(result.Value.CleanupPath));
                // Launch the replaced canonical image too, rather than relying only on a version mock.
                using var next = Process.Start(new ProcessStartInfo(paths.RcloneExecutable)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                await next.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, next.ExitCode);
            }
            process.Kill();
            await process.WaitForExitAsync(deadline.Token);
            await service.CheckAsync(deadline.Token);
            Assert.Empty(Directory.EnumerateFiles(paths.Rclone, ".rclone-*.exe"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            process.Dispose();
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(paths.Root, true); break; }
                catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(50); }
            }
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Only release discovery/download/version responses are simulated. Replacement,
    // rollback, file locks, process identity, launches and backup cleanup use Windows.
    private sealed class ImageRunner(string replacement, bool rejectInstalled) : IRcloneProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.SequenceEqual(["version", "--check"]))
                return Task.FromResult(new ProcessRunResult(0, "yours: 1.75.0\nlatest: 1.76.0", "", false));
            if (arguments.SequenceEqual(["version"]))
            {
                var updated = Hash(executablePath) == Hash(replacement);
                return Task.FromResult(updated && rejectInstalled && Path.GetFileName(executablePath) == "rclone.exe"
                    ? new ProcessRunResult(1, "", "Verification rejected", false)
                    : new ProcessRunResult(0, updated ? "rclone v1.76.0" : "rclone v1.75.0", "", false));
            }
            var output = Array.IndexOf(arguments.ToArray(), "--output");
            Assert.True(output >= 0);
            File.Copy(replacement, arguments[output + 1]);
            return Task.FromResult(new ProcessRunResult(0, "", "", false));
        }
    }
}
