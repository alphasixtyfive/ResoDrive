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

        var diagnostics = new ProcessDiagnosticLog(paths, "host");
        diagnostics.StartSession();
        try
        {
            do
            {
                var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
                // Default console/event-log providers can retain unsanitized account details.
                // Use the same bounded, guarded local policy for every host diagnostic.
                builder.Logging.ClearProviders();
                var provider = new HostDiagnosticLoggerProvider(diagnostics);
                builder.Logging.AddProvider(provider);
                builder.Logging.AddFilter<HostDiagnosticLoggerProvider>(static (_, level) =>
                    level >= LogLevel.Information && level != LogLevel.None);
                builder.Services.Configure<HostOptions>(static options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
                builder.Services.AddSingleton(paths);
                builder.Services.AddHostedService<Worker>();
                var host = builder.Build();
                var worker = host.Services.GetServices<IHostedService>().OfType<Worker>().Single();
                using var started = host.Services.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStarted.Register(() => diagnostics.Information("host.started"));
                await host.RunAsync(cancellationToken).ConfigureAwait(false);
                if (provider.FatalBackgroundException is { } fatal)
                    throw new InvalidOperationException("The ResoDrive background host failed unexpectedly.", fatal);
                if (!worker.CanResumeRemoteWipe || !worker.RestartForRemoteWipe) break;
                // A new lifetime must capture the current wipe generation. Old guards
                // deliberately refuse to recreate deleted account diagnostics.
                diagnostics = new ProcessDiagnosticLog(paths, "host");
            } while (!cancellationToken.IsCancellationRequested &&
                new RemoteWipeStateStore(paths).Read() is { Phase: not RemoteWipePhase.Completed });
            diagnostics.Information("host.stopped");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Information("host.cancelled");
            throw;
        }
#pragma warning disable CA1031 // Log, then rethrow the original top-level host failure.
        catch (Exception exception)
        {
            diagnostics.Exception("host.fatal", exception);
            throw;
        }
#pragma warning restore CA1031
    }
}
