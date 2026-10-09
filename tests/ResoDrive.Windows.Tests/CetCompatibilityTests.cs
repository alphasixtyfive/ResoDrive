using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace ResoDrive.Windows.Tests;

public sealed partial class CetCompatibilityTests
{
    private static readonly string Installed = Path.Combine(Path.GetTempPath(), "installed", "resodrive.exe");

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void OnlyExplicitInstalledChoiceDisablesCet(string? value, bool expected)
    {
        Assert.Equal(expected, CetCompatibility.IsEnabled(Installed, Installed, value));
        Assert.False(CetCompatibility.IsEnabled(Installed, null, value));
        Assert.False(CetCompatibility.IsEnabled(Path.Combine(Path.GetTempPath(), "portable", "resodrive.exe"), Installed, value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData(" 1")]
    public void InvalidInstalledChoiceRequiresRepair(object value)
    {
        Assert.Throws<InvalidDataException>(() => CetCompatibility.IsEnabled(Installed, Installed, value));
        Assert.False(CetCompatibility.IsEnabled(Path.Combine(Path.GetTempPath(), "portable", "resodrive.exe"), Installed, value));
    }

    [Fact]
    public void ExpandedStringIsNotAnInstalledCompatibilityChoice()
    {
        Assert.Throws<InvalidDataException>(() =>
            CetCompatibility.IsEnabled(Installed, Installed, "1", RegistryValueKind.ExpandString));
    }

    [Fact]
    public void ArgumentListRoundTripsThroughTheWindowsParser()
    {
        var start = new ProcessStartInfo(Installed);
        string[] arguments = ["", "ordinary", "with space", "a\"b", @"trailing space\", "日本語", @"\\server\path"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var pointer = CommandLineToArgvW(CetProcessLauncher.BuildCommandLine(start), out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var actual = Enumerable.Range(0, count).Select(index =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))).ToArray();
            Assert.Equal(new[] { Installed }.Concat(arguments), actual);
        }
        finally { _ = LocalFree(pointer); }
    }

    [Fact]
    public void NativeLaunchPreservesEnvironmentAndWorkingDirectoryAndDisablesShadowStacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-cet-launch-" + Guid.NewGuid().ToString("N"), "space 日本語");
        Directory.CreateDirectory(directory);
        SafeProcessHandle? child = null;
        try
        {
            var result = Path.Combine(directory, "result.txt");
            var value = @"C:\handoff with space\日本語\";
            var script = "[IO.File]::WriteAllText($env:RDRIVE_TEST_RESULT, ($env:RDRIVE_UPDATE_HANDOFF_DIR + [char]10 + (Get-Location).Path + [char]10 + [Environment]::GetEnvironmentVariables().Contains('RDRIVE_TEST_REMOVED')))";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.Environment["RDRIVE_UPDATE_HANDOFF_DIR"] = value;
            start.Environment["RDRIVE_TEST_RESULT"] = result;
            start.Environment["RDRIVE_TEST_REMOVED"] = null;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                using var legacy = Assert.IsType<Process>(CetProcessLauncher.Start(start, disableCet: true));
                Assert.True(legacy.WaitForExit(15000));
                Assert.Equal(0, legacy.ExitCode);
                Assert.Equal(value + "\n" + directory + "\nFalse", File.ReadAllText(result));
                return;
            }
            child = Assert.IsType<SafeProcessHandle>(CetProcessLauncher.Start(start, disableCet: true));
            Assert.True(GetProcessMitigationPolicy(child, 15, out var policy, sizeof(uint)));
            Assert.Equal(0u, policy & 1); // EnableUserShadowStack
            Assert.Equal(0u, WaitForSingleObject(child, 15000));
            Assert.True(GetExitCodeProcess(child, out var code));
            Assert.Equal(0u, code);
            Assert.Equal(value + "\n" + directory + "\nFalse", File.ReadAllText(result));
        }
        finally
        {
            if (child is not null)
            {
                if (WaitForSingleObject(child, 0) != 0)
                {
                    _ = TerminateProcess(child, 1); // Only the exact disposable child created above.
                    _ = WaitForSingleObject(child, 5000);
                }
                child.Dispose();
            }
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint flags, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint code);
}
