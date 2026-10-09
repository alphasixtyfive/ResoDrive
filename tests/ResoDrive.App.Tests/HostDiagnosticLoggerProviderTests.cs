using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class HostDiagnosticLoggerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-host-log-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealGenericHostBackgroundServiceFailureIsReportedAsFatal(bool externalLoggingDisabled)
    {
        // Account lease contention has separate coverage. This fixture verifies
        // generic-host routing, filtering and redaction using its private log.
        var diagnostic = new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(diagnostic);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        if (externalLoggingDisabled)
        {
            builder.Configuration["Logging:LogLevel:Default"] = "None";
            builder.Configuration["Logging:LogLevel:Microsoft.Extensions.Hosting.Internal.Host"] = "None";
        }
        builder.Logging.ClearProviders().AddProvider(provider);
        builder.Logging.AddFilter<HostDiagnosticLoggerProvider>(static (_, level) =>
            level >= LogLevel.Information && level != LogLevel.None);
        var reportFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddHostedService(_ => new FailingWorker(reportFailure.Task));
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        // Startup lifecycle logging is complete before the worker reports its
        // failure; a fixed delay could overlap those best-effort file writes.
        reportFailure.SetResult();
        await host.WaitForShutdownAsync(timeout.Token);

        Assert.IsType<InvalidOperationException>(provider.FatalBackgroundException);
        var contents = File.ReadAllText(diagnostic.LogFile);
        Assert.Contains("eventId=10", contents, StringComparison.Ordinal);
        Assert.Contains("errorId=", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("DO-NOT-EXPORT", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverableOperationExceptionAndRequestedShutdownAreNotFatal()
    {
        var diagnostic = new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(diagnostic);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(provider);
        var reportFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddHostedService(services => new RecoverableWorker(
            services.GetRequiredService<ILogger<RecoverableWorker>>(),
            services.GetRequiredService<IHostApplicationLifetime>(), reportFailure.Task));
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        reportFailure.SetResult();
        await host.WaitForShutdownAsync(timeout.Token);

        Assert.Null(provider.FatalBackgroundException);
        Assert.Contains("eventId=1004", File.ReadAllText(diagnostic.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public void FaultedEventAloneAndForeignCategoryCannotTriggerFatalShutdown()
    {
        using var provider = new HostDiagnosticLoggerProvider(new ProcessDiagnosticLog(new ApplicationPaths(_root), "host"));
        var exception = new InvalidOperationException("recoverable");
        var logger = provider.CreateLogger("Microsoft.Extensions.Hosting.Internal.Host");
        logger.Log(LogLevel.Error, new EventId(9, "BackgroundServiceFaulted"), "failure", exception, static (state, _) => state);
        var other = provider.CreateLogger("ResoDrive.Host.Worker");
        other.Log(LogLevel.Critical, new EventId(10, "BackgroundServiceStoppingHost"), "failure", exception, static (state, _) => state);
        Assert.Null(provider.FatalBackgroundException);
    }

    [Fact]
    public void FormatterAndDestinationFailureCannotBreakWorkerLogging()
    {
        var paths = new ApplicationPaths(_root);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(paths.Logs, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(new ProcessDiagnosticLog(paths, "host"));
        var logger = provider.CreateLogger("ResoDrive.Host.Worker");
        logger.Log(LogLevel.Error, new EventId(1004), "failure", new InvalidOperationException("original"),
            static (_, _) => throw new IOException("formatter failed"));
        Assert.Null(provider.FatalBackgroundException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FailingWorker(Task reportFailure) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await reportFailure.WaitAsync(stoppingToken);
            throw new InvalidOperationException("token=DO-NOT-EXPORT");
        }
    }

    private sealed class RecoverableWorker(ILogger<RecoverableWorker> logger, IHostApplicationLifetime lifetime, Task reportFailure)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await reportFailure.WaitAsync(stoppingToken);
            logger.Log(LogLevel.Error, new EventId(1004, "OperationFailure"), "recoverable upload error",
                new IOException("token=DO-NOT-EXPORT"), static (state, _) => state);
            lifetime.StopApplication();
        }
    }
}
