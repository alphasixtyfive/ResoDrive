using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResoDrive.Windows;

internal static partial class InstallerProcessInspection
{
    private static bool BelongsToOtherAccount(Process process, SafeProcessHandle handle, CancellationToken token)
    {
        try { return !VerifyAccountOrExited(process, handle, token); }
        catch (OtherAccountException) { return true; }
    }

    // The retained handle prevents PID reuse. An account-read failure never means
    // "foreign account" or "safe to stop": only exit of this exact process can
    // make the failed inspection irrelevant. A still-live process fails closed.
    private static bool VerifyAccountOrExited(Process process, SafeProcessHandle handle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (HasConfirmedExit(handle)) return false;
        try
        {
            VerifySameAccount(handle);
            return !HasConfirmedExit(handle);
        }
        catch (OtherAccountException) { throw; }
        catch (IOException exception)
        {
            if (HasConfirmedExit(handle)) return false;
            throw new IOException(AccountUnknownMessage,
                new IOException($"Account inspection failed for process {process.Id} ({process.ProcessName}).", exception));
        }
    }

    private static bool HasConfirmedExit(SafeProcessHandle handle) =>
        !handle.IsInvalid && GetExitCodeProcess(handle, out var code) &&
        // Exit code 259 is ambiguous. A query-only handle fails closed here;
        // a synchronized UI handle can also confirm exit by waiting.
        (code != 259 || WaitForSingleObject(handle, 0) == 0);

    private static IOException InspectionFailure(Process process, string stage, int error, string message) =>
        new(message, new Win32Exception(error,
            $"Process {process.Id} ({process.ProcessName}): {stage} failed with Windows error {error}."));

    private static unsafe string ReadExecutablePath(SafeProcessHandle handle)
    {
        var buffer = new char[32768];
        var length = (uint)buffer.Length;
        fixed (char* pointer = buffer)
        {
            if (!QueryFullProcessImageNameW(handle, 0, pointer, ref length))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new string(pointer, 0, (int)length);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, char* path, ref uint length);
}
