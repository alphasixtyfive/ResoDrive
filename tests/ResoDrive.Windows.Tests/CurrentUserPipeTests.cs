using System.IO.Pipes;
using System.Security.Principal;

namespace ResoDrive.Windows.Tests;

public sealed class CurrentUserPipeTests
{
    [Fact]
    public async Task ServerIsOwnedByAccountSidAndClientValidatesActualServerProcess()
    {
        var name = "resodrive-pipe-test-" + Guid.NewGuid().ToString("N");
        using var server = CurrentUserPipe.CreateServer(name);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, server.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        using var client = CurrentUserPipe.CreateClient(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        CurrentUserPipe.ValidateServerIdentity(client);
        Assert.Equal(Environment.ProcessId, CurrentUserPipe.GetServerProcessId(client));
    }

    [Fact]
    public async Task LegacyPipeIsAcceptedByAccountIdentityEvenWhenTokenDefaultOwnerDiffers()
    {
        var name = "resodrive-legacy-pipe-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = CurrentUserPipe.CreateClient(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        CurrentUserPipe.ValidateServerIdentity(client);
    }
}
