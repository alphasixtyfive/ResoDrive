using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ResoDrive.Windows.Tests;

/// <summary>Optional local RC interoperability test. Every configuration and credential is disposable.</summary>
public sealed class RcloneRemoteConfigurationIntegrationTests
{
    [RcloneFixtureFact]
    public async Task RealRclone_ReadsOnlySelectedRemotesAndPreservesEncryptedConfig()
    {
        var executable = Environment.GetEnvironmentVariable("RDRIVE_TEST_RCLONE")!;
        var root = Path.Combine(Path.GetTempPath(), $"rdrive-rclone-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "rclone.conf");
        var passwordScript = Path.Combine(root, "fixture-password.cmd");
        try
        {
            await File.WriteAllTextAsync(passwordScript, "@echo off\r\necho disposable-encryption-password\r\n", Encoding.ASCII);
            var passwordCommand = $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")}\" /d /q /c \"{passwordScript}\"";
            await File.WriteAllTextAsync(config, """
                [cloud]
                type = webdav
                url = https://example.test/nextcloud/remote.php/dav/files/user/
                vendor = nextcloud
                user = test-user
                pass = YWFhYWFhYWFhYWFhYWFhYXMaGgIlEQ
                pacer_min_sleep = 100ms

                [other]
                type = sftp
                host = example.test
                user = unchanged-user
                pass = YmJiYmJiYmJiYmJiYmJiYp3gcEWbAw
                port = 2222
                """);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase) ||
                key == "_RCLONE_CONFIG_KEY_FILE").ToArray()) start.Environment.Remove(key);
            foreach (var argument in new[] { "--config", config, "--password-command", passwordCommand, "config", "encryption", "set" })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                await Task.WhenAll(output, error);
                Assert.Equal(0, process.ExitCode);
            }
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(config));
            var access = new RcloneRemoteConfigurationAccess();
            var before = await access.ReadAsync(executable, config, passwordCommand, ["cloud", "other", "missing"], default);
            Assert.Equal(2, before.Count);
            Assert.Equal("potato", RcloneObscuredSecret.Reveal(before["cloud"]["pass"]));
            Assert.Equal("nextcloud", before["cloud"]["vendor"]);
            Assert.Equal("unchanged-user", before["other"]["user"]);
            Assert.Equal("2222", before["other"]["port"]);
            Assert.Equal("potato", RcloneObscuredSecret.Reveal(before["other"]["pass"]));
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(config)));
            Assert.StartsWith("# Encrypted rclone configuration File", await File.ReadAllTextAsync(config), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RcloneFixtureFactAttribute : FactAttribute
    {
        public RcloneFixtureFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("RDRIVE_TEST_RCLONE")))
                Skip = "Set RDRIVE_TEST_RCLONE to a trusted rclone executable to run the disposable config-only RC test.";
        }
    }
}
