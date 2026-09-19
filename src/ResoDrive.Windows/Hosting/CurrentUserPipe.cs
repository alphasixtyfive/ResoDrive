using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ResoDrive.Windows;

/// <summary>Uses the account SID across UAC elevation, never the token's default owner (Administrators).</summary>
public static partial class CurrentUserPipe
{
    public static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The Windows account could not be identified.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    public static NamedPipeClientStream CreateClient(string name) => new(
        ".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

    public static void ValidateServerIdentity(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using var identity = WindowsIdentity.GetCurrent();
        using var process = OpenProcess(0x1000, false, GetServerProcessId(pipe)); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
            throw new UnauthorizedAccessException("The ResoDrive background host identity could not be verified.");
        using (token)
        using (var serverIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        if (identity.User is null || serverIdentity.User is null || !identity.User.Equals(serverIdentity.User))
            throw new UnauthorizedAccessException("The ResoDrive background host belongs to a different Windows account. Close that copy before updating.");
    }

    public static int GetServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((int)processId);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
