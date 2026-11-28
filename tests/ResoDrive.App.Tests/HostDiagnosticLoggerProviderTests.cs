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
    [InlineData("ResoDrive.Host.Worker", 1001, "host.worker.initialization_failed")]
    [InlineData("ResoDrive.Host.Worker", 1003, "host.worker.operation_failed")]
    [InlineData("ResoDrive.Host.Worker", 1014, "host.worker.runtime_identity_recovered")]
    [InlineData("ResoDrive.Host.Worker", 1011, "host.log")]
    [InlineData("Other.Worker", 1003, "host.log")]
    public void KnownWorkerEventsRemainClassifiableAfterMessageRedaction(string category, int id, string expectedEvent)
    {
        var diagnostic = new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(diagnostic);
        var logger = provider.CreateLogger(category);
        logger.Log(LogLevel.Warning, new EventId(id),
            "token=DO-NOT-EXPORT https://private.example/secret C:\\private\\file.txt",
            exception: null, static (state, _) => state);

        var contents = File.ReadAllText(diagnostic.LogFile);
        Assert.Contains($"event={expectedEvent} ", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("DO-NOT-EXPORT", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealGenericHostBackgroundServiceFailurePropagatesAfterCleanupAndRemainsRedacted(bool externalLoggingDisabled)
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
        builder.Services.Configure<HostOptions>(static options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        var reportFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("token=DO-NOT-EXPORT");
        var cleanup = new StopProbe();
        builder.Services.AddHostedService(_ => new FailingWorker(reportFailure.Task, failure));
        builder.Services.AddSingleton<IHostedService>(cleanup);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        // Startup lifecycle logging is complete before the worker reports its
        // failure; a fixed delay could overlap those best-effort file writes.
        reportFailure.SetResult();
        var propagated = await Assert.ThrowsAsync<InvalidOperationException>(() => host.WaitForShutdownAsync(timeout.Token));

        Assert.Same(failure, propagated);
        Assert.True(cleanup.Stopped);
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
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

        var contents = File.ReadAllText(diagnostic.LogFile);
        Assert.Contains("eventId=1004", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("CRITICAL", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("DO-NOT-EXPORT", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Hosting.Internal.Host", 9)]
    [InlineData("ResoDrive.Host.Worker", 10)]
    public async Task LoggedExceptionsWithoutAWorkerFaultDoNotStopTheHost(string category, int eventId)
    {
        using var provider = new HostDiagnosticLoggerProvider(new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log")));
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(provider);
        var cleanup = new StopProbe();
        builder.Services.AddSingleton<IHostedService>(cleanup);
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var loggedFailure = new InvalidOperationException("recoverable");
        provider.CreateLogger(category).Log(LogLevel.Critical, new EventId(eventId, "BackgroundServiceStoppingHost"),
            "failure", loggedFailure, static (state, _) => state);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(cleanup.Stopped);
        lifetime.StopApplication();
        await host.WaitForShutdownAsync(timeout.Token);
        Assert.True(cleanup.Stopped);
    }

    [Fact]
    public async Task MultipleWorkerFailuresRemainObservableAfterAllServicesStop()
    {
        var diagnostic = new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(diagnostic);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(provider);
        var reportFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new InvalidOperationException("token=DO-NOT-EXPORT-FIRST");
        var second = new IOException("token=DO-NOT-EXPORT-SECOND");
        var cleanup = new StopProbe();
        builder.Services.AddSingleton<IHostedService>(new FailingWorker(reportFailure.Task, first));
        builder.Services.AddSingleton<IHostedService>(new FailingWorker(reportFailure.Task, second));
        builder.Services.AddSingleton<IHostedService>(cleanup);
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        reportFailure.SetResult();

        var propagated = await Assert.ThrowsAsync<AggregateException>(() => host.WaitForShutdownAsync(timeout.Token));
        Assert.Equal(2, propagated.InnerExceptions.Count);
        Assert.Contains(first, propagated.InnerExceptions);
        Assert.Contains(second, propagated.InnerExceptions);
        Assert.True(cleanup.Stopped);
        Assert.DoesNotContain("DO-NOT-EXPORT", File.ReadAllText(diagnostic.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationStopsServicesWithoutInventingAFatalFailure()
    {
        var diagnostic = new ProcessDiagnosticLog(Path.Combine(_root, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(diagnostic);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(provider);
        var cleanup = new StopProbe();
        var waiting = new CancellationWorker();
        builder.Services.AddSingleton<IHostedService>(waiting);
        builder.Services.AddSingleton<IHostedService>(cleanup);
        using var host = builder.Build();
        using var cancellation = new CancellationTokenSource();
        await host.StartAsync(cancellation.Token);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var shutdown = host.WaitForShutdownAsync(cancellation.Token);
        cancellation.Cancel();

        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cleanup.Stopped);
        Assert.True(waiting.Cancelled);
        Assert.DoesNotContain("CRITICAL", File.ReadAllText(diagnostic.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatterAndDestinationFailureCannotBreakWorkerLogging()
    {
        var paths = new ApplicationPaths(_root);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(paths.Logs, "resodrive-host.log"));
        using var provider = new HostDiagnosticLoggerProvider(new ProcessDiagnosticLog(paths, "host"));
        var logger = provider.CreateLogger("ResoDrive.Host.Worker");
        Assert.Null(Record.Exception(() => logger.Log(LogLevel.Error, new EventId(1004), "failure",
            new InvalidOperationException("original"), static (_, _) => throw new IOException("formatter failed"))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FailingWorker(Task reportFailure, Exception failure) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Once released, each worker must report its actual failure even if
            // another worker has already requested shutdown.
            await reportFailure;
            throw failure;
        }
    }

    private sealed class StopProbe : IHostedService
    {
        public bool Stopped { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationWorker : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            finally { Cancelled = stoppingToken.IsCancellationRequested; }
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
