using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResoDrive.Windows;

namespace ResoDrive.CetUpdateLauncher;

internal static partial class Program
{
    private static int Main(string[] arguments)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
                throw new InvalidOperationException("CET update acceptance requires a disposable hosted runner.");
            if (arguments.Length < 4) throw new ArgumentException("Expected installed path, child path, working directory and receipt path.");
            var installed = Path.GetFullPath(arguments[0]);
            var executable = Path.GetFullPath(arguments[1]);
            var directory = Path.GetFullPath(arguments[2]);
            var receipt = Path.GetFullPath(arguments[3]);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            };
            foreach (var argument in arguments.Skip(4)) start.ArgumentList.Add(argument);
            var preference = CetCompatibility.IsEnabledForInstalledApplication(installed);
            using var child = CetCompatibility.StartInstalledHelper(start, installed)
                ?? throw new InvalidOperationException("The production CET launcher returned no process.");
            var handle = child is Process process ? process.SafeHandle : (SafeProcessHandle)child;
            if (!GetProcessMitigationPolicy(handle, 15, out var policy, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the disposable child CET policy.");
            string executableHash;
            using (var bytes = File.OpenRead(executable)) executableHash = Convert.ToHexString(SHA256.HashData(bytes));
            File.WriteAllText(receipt + ".tmp", JsonSerializer.Serialize(new
            {
                ChildProcessId = GetProcessId(handle), DisabledForRegisteredInstallation = preference,
                EffectiveShadowStackFlags = policy, ShadowStacksEnabled = (policy & 1) != 0,
                ExecutablePath = executable, ExecutableSha256 = executableHash,
            }));
            File.Move(receipt + ".tmp", receipt, overwrite: true);
            if (WaitForSingleObject(handle, 360000) != 0)
                throw new TimeoutException("The disposable child did not complete within six minutes.");
            if (!GetExitCodeProcess(handle, out var code))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the disposable child exit code.");
            return unchecked((int)code);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint flags, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
