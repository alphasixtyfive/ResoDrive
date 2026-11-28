using System.IO;
using System.IO.Pipes;
using ResoDrive.Windows;

namespace ResoDrive.App;

/// <summary>Keeps the original window alive until its copied helper can accept the update.</summary>
internal static class UpdateHelperReadiness
{
    internal const string EnvironmentVariable = "RDRIVE_UPDATE_READY_PIPE";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal static async Task AcceptAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        var message = new byte[1];
        await pipe.ReadExactlyAsync(message, token).ConfigureAwait(false);
        if (message[0] != 1) throw new IOException("The update helper sent an invalid readiness response.");
        await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task SignalAsync(string name, int parentProcessId, CancellationToken token)
    {
        using var pipe = CurrentUserPipe.CreateClient(name);
        await pipe.ConnectAsync(token).ConfigureAwait(false);
        CurrentUserPipe.ValidateServerIdentity(pipe);
        if (CurrentUserPipe.GetServerProcessId(pipe) != parentProcessId)
            throw new IOException("The update helper could not verify the original application.");
        await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
        var accepted = new byte[1];
        await pipe.ReadExactlyAsync(accepted, token).ConfigureAwait(false);
        if (accepted[0] != 1) throw new IOException("The original application did not accept the update handoff.");
    }
}
