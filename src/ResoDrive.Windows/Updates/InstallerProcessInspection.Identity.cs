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
    internal static bool VerifyAccountOrExited(Process process, SafeProcessHandle handle, CancellationToken token,
        Action<SafeProcessHandle>? inspectAccount = null)
    {
        token.ThrowIfCancellationRequested();
        if (HasConfirmedExit(handle)) return false;
        try
        {
            (inspectAccount ?? VerifySameAccount)(handle);
            return !HasConfirmedExit(handle);
        }
        catch (OtherAccountException) { throw; }
        catch (IOException exception)
        {
            if (WaitForConfirmedExit(handle, token)) return false;
            throw new IOException(AccountUnknownMessage,
                new IOException($"Account inspection failed for process {process.Id} ({process.ProcessName}).", exception));
        }
    }

    private static bool WaitForConfirmedExit(SafeProcessHandle handle, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        do
        {
            token.ThrowIfCancellationRequested();
            if (HasConfirmedExit(handle)) return true;
            if (Stopwatch.GetTimestamp() >= deadline) return false;
            // Query-only handles deliberately have no SYNCHRONIZE permission.
            // Poll the exact handle's exit code, without reopening by PID.
            if (token.WaitHandle.WaitOne(25)) token.ThrowIfCancellationRequested();
        } while (true);
    }

    private static bool HasConfirmedExit(SafeProcessHandle handle) =>
        !handle.IsInvalid && GetExitCodeProcess(handle, out var code) &&
        (code != 259 || WaitForSingleObject(handle, 0) == 0); // STILL_ACTIVE can also be a real exit code.

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
