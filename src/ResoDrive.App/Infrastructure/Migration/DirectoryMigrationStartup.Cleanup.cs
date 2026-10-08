using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static partial class DirectoryMigrationStartup
{
    internal const string CleanupArgument = "--cleanup-user-migration-helper";
    private static string HelperPath => Path.Combine(StateDirectory, "resodrive-migration-helper.exe");

    internal static async Task<int> CleanupHelperAsync(int parentProcessId)
    {
        try
        {
            if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId) return 1;
            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                try
                {
                    if (!parent.HasExited)
                    {
                        CurrentUserPipe.ValidateProcessIdentity(parent.Id);
                        if (!InstallationDirectories.SamePath(parent.MainModule?.FileName ?? "", HelperPath)) return 1;
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (parent.HasExited &&
                    exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                { /* It exited between opening the process and inspecting its image. */ }
            }
            catch (ArgumentException) { /* The originating helper already exited. */ }
            _ = UserDataDirectoryMigration.PrepareHelperDirectory();
            if (!File.Exists(HelperPath)) return 0;
            if ((File.GetAttributes(HelperPath) & FileAttributes.ReparsePoint) != 0) return 1;
            using (var expected = File.OpenRead(InstallationDirectories.Executable))
            using (var copy = File.OpenRead(HelperPath))
                if (!SHA256.HashData(copy).AsSpan().SequenceEqual(SHA256.HashData(expected))) return 1;
            File.Delete(HelperPath);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { return 1; } // Preserve an unverifiable copy; never delete a different file.
    }
}
