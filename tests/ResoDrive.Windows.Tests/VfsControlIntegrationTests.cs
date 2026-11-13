using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ResoDrive.Windows.Tests;

/// <summary>Real VFS/RC interoperability using only a disposable local directory; no WinFsp or remote account.</summary>
[Collection(MountRecoveryInspectionGroup.Name)]
public sealed class VfsControlIntegrationTests
{
    [RcloneTheory]
    [InlineData("off")]
    [InlineData("full")]
    public async Task RealEngineReportsIdleAndAcceptsAuthenticatedQuit(string cacheMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "resodrive-vfs-interop", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(cache);
        var config = Path.Combine(root, "rclone.conf");
        await File.WriteAllTextAsync(config, "");
        await File.WriteAllTextAsync(Path.Combine(data, "sentinel.txt"), "disposable local data");
        var password = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("RDRIVE_TEST_RCLONE")!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase) ||
                     key == "_RCLONE_CONFIG_KEY_FILE").ToArray()) start.Environment.Remove(key);
        start.Environment["RCLONE_RC_USER"] = "fixture";
        start.Environment["RCLONE_RC_PASS"] = password;
        foreach (var argument in new[] { "serve", "webdav", data, "--addr", "127.0.0.1:0",
                     "--rc", "--rc-addr", "127.0.0.1:0", "--config", config,
                     "--cache-dir", cache, "--vfs-cache-mode", cacheMode, "--log-level", "INFO" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        Task? output = null;
        Task? error = null;
        var started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            output = process.StandardOutput.ReadToEndAsync();
            error = ProcessRunner.ReadBoundedLinesAsync(process.StandardError, line =>
            {
                if (!line.Contains("Serving remote control on", StringComparison.Ordinal)) return;
                var match = Regex.Match(line, @"http://(127\.0\.0\.1:\d+)/");
                if (match.Success) address.TrySetResult(match.Groups[1].Value);
            }, CancellationToken.None);
            var endpoint = await address.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            VfsTransferStatus? status;
            do
            {
                status = await VfsStatusReader.ReadAsync(endpoint, "fixture", password, cache, deadline.Token);
                if (status is null) await Task.Delay(100, deadline.Token);
            } while (status is null);

            Assert.False(status.NeedsAttention);
            Assert.Equal(cacheMode != "off", status.UsesDiskCache);
            Assert.False(await VfsStatusReader.RequestQuitAsync(endpoint, "fixture", "wrong-password", deadline.Token));
            Assert.False(process.HasExited);
            Assert.True(await VfsStatusReader.RequestQuitAsync(endpoint, "fixture", password, deadline.Token));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("disposable local data", await File.ReadAllTextAsync(Path.Combine(data, "sentinel.txt")));
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (output is not null && error is not null) await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RcloneTheoryAttribute : TheoryAttribute
    {
        public RcloneTheoryAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("RDRIVE_TEST_RCLONE")))
                Skip = "Set RDRIVE_TEST_RCLONE to a trusted rclone executable for disposable local VFS/RC checks.";
        }
    }
}
