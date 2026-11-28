using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResoDrive.Windows;

internal static partial class CetProcessLauncher
{
    internal static unsafe IDisposable? Start(ProcessStartInfo startInfo, bool disableCet)
    {
        // Windows versions before 2004 cannot enable CET shadow stacks.
        if (!disableCet || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return Process.Start(startInfo);
        if (startInfo.UseShellExecute || startInfo.RedirectStandardInput || startInfo.RedirectStandardOutput ||
            startInfo.RedirectStandardError || !string.IsNullOrEmpty(startInfo.UserName) ||
            !Path.IsPathFullyQualified(startInfo.FileName))
            throw new InvalidOperationException("The CET compatibility helper requires a direct application launch.");

        nuint attributeBytes = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
        if (attributeBytes == 0) throw NativeFailure("InitializeProcThreadAttributeList");
        var attributes = Marshal.AllocHGlobal(checked((nint)attributeBytes));
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes))
                throw NativeFailure("InitializeProcThreadAttributeList");
            initialized = true;
            // First policy word is unchanged. The second disables only CET user shadow stacks.
            var policies = stackalloc ulong[] { 0, 2UL << 28 };
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020007, (IntPtr)policies, 16, IntPtr.Zero, IntPtr.Zero))
                throw NativeFailure("UpdateProcThreadAttribute(CET)");
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributes,
            };
            var command = BuildCommandLine(startInfo).ToCharArray();
            var environment = BuildEnvironment(startInfo).ToCharArray();
            fixed (char* commandPointer = command)
            fixed (char* environmentPointer = environment)
            {
                if (!CreateProcessW(startInfo.FileName, commandPointer, IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000u | 0x00000400u | (startInfo.CreateNoWindow ? 0x08000000u : 0),
                    environmentPointer, string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startup, out var process))
                    throw NativeFailure("CreateProcess(CET compatibility helper)");
                // Retain the exact native process handle; reopening its PID can race a fast helper exit.
                using var thread = new SafeWaitHandle(process.Thread, ownsHandle: true);
                return new SafeProcessHandle(process.Process, ownsHandle: true);
            }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var command = new StringBuilder(QuoteArgument(startInfo.FileName));
        if (startInfo.ArgumentList.Count > 0 && !string.IsNullOrEmpty(startInfo.Arguments))
            throw new InvalidOperationException("Specify either helper arguments or an argument list.");
        if (!string.IsNullOrEmpty(startInfo.Arguments)) command.Append(' ').Append(startInfo.Arguments);
        foreach (var argument in startInfo.ArgumentList) command.Append(' ').Append(QuoteArgument(argument));
        if (command.Length >= 32767 || command.ToString().Contains('\0'))
            throw new InvalidOperationException("The helper command line is invalid or too long.");
        return command.Append('\0').ToString();
    }

    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string BuildEnvironment(ProcessStartInfo startInfo)
    {
        var environment = new StringBuilder();
        foreach (var variable in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (variable.Value is null) continue; // Match Process.Start: null removes an inherited variable.
            if (variable.Key.Contains('\0') || variable.Value?.Contains('\0') == true)
                throw new InvalidOperationException("The helper environment contains an invalid value.");
            environment.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
        }
        if (environment.Length == 0) environment.Append('\0');
        return environment.Append('\0').ToString();
    }

    private static Win32Exception NativeFailure(string stage)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{stage} failed with Windows error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr attributes, uint count, uint flags, ref nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute,
        IntPtr value, nuint bytes, IntPtr previousValue, IntPtr returnBytes);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string application, char* command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        char* environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
}
