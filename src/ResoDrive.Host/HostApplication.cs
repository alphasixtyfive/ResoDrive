using ResoDrive.Windows;

namespace ResoDrive.Host;

public static class HostApplication
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var paths = new ApplicationPaths();
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            $"Local\\{HostProtocol.GetPipeName(paths)}",
            out var createdNew);
        if (!createdNew)
            return;

        do
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
            builder.Services.AddSingleton(paths);
            builder.Services.AddHostedService<Worker>();
            var host = builder.Build();
            var worker = host.Services.GetServices<IHostedService>().OfType<Worker>().Single();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            if (!worker.CanResumeRemoteWipe || !worker.RestartForRemoteWipe) break;
        } while (!cancellationToken.IsCancellationRequested &&
            new RemoteWipeStateStore(paths).Read() is { Phase: not RemoteWipePhase.Completed });
    }
}
