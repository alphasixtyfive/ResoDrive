using System.IO;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class SetupCommitTransactionTests
{
    [Fact]
    public async Task FailedSaveWaitsForRecoveryAndRestoresPublishedFilesBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "resodrive-setup-outcome-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var staged = Path.Combine(root, "staged");
            var destination = Path.Combine(root, "config");
            await File.WriteAllTextAsync(staged, "new connection");
            await File.WriteAllTextAsync(destination, "previous connection");
            using var files = new SetupFileTransaction([(staged, destination)]);
            var calls = new List<string>();
            var rollbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = Operations(calls) with
            {
                PublishFiles = () => { calls.Add("publish"); files.Apply(); },
                SaveSettings = () => Fail(calls, "save", "Settings disk is unavailable."),
                RestoreStartup = async () =>
                {
                    calls.Add("restore startup");
                    rollbackEntered.SetResult();
                    await allowRollback.Task;
                    return Result.Success();
                },
                RestoreFiles = () => { calls.Add("restore files"); files.Rollback(); },
            };
            var pending = SetupCommitTransaction.ApplyAsync(operations);
            await rollbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal("new connection", await File.ReadAllTextAsync(destination));
            allowRollback.SetResult();
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(outcome.Committed);
            Assert.Equal("Settings disk is unavailable.", outcome.Failure);
            Assert.Equal("previous connection", await File.ReadAllTextAsync(destination));
            Assert.Equal(["publish", "startup", "save", "restore startup", "restore files"], calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ActivationFailurePreservesPrimaryAndReportsBothStartupAndHostRecoveryFailures()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            ActivateSettings = () => Fail(calls, "activate", "New connection was rejected."),
            RestoreStartup = () => Fail(calls, "restore startup", "Startup task access denied."),
            ReactivateSettings = () => Fail(calls, "reactivate", "Host connection lost."),
        });

        Assert.False(outcome.Committed);
        Assert.StartsWith("New connection was rejected.", outcome.Failure, StringComparison.Ordinal);
        Assert.Contains("Startup task access denied.", outcome.Failure, StringComparison.Ordinal);
        Assert.Contains("Host connection lost.", outcome.Failure, StringComparison.Ordinal);
        Assert.Equal(["publish", "startup", "save", "activate", "restore settings", "restore startup", "restore files", "reactivate"], calls);
    }

    [Fact]
    public async Task FailedSettingsRestorationKeepsNewConnectionFilesAndStartupTogether()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            ActivateSettings = () => Fail(calls, "activate", "Activation timed out."),
            RestoreSettings = () => Fail(calls, "restore settings", "Saved settings cannot be restored."),
        });

        Assert.False(outcome.Committed);
        Assert.StartsWith("Activation timed out.", outcome.Failure, StringComparison.Ordinal);
        Assert.Contains("saved connection files were kept together", outcome.Failure, StringComparison.Ordinal);
        Assert.Equal(["publish", "startup", "save", "activate", "restore settings", "complete"], calls);
    }

    [Fact]
    public async Task FileRollbackExceptionKeepsPrimaryFailureAndDoesNotActivateAnUnrestoredConfig()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            ActivateSettings = () => Fail(calls, "activate", "New connection was rejected."),
            RestoreFiles = () => { calls.Add("restore files"); throw new IOException("Recovery backup is unavailable."); },
        });

        Assert.StartsWith("New connection was rejected.", outcome.Failure, StringComparison.Ordinal);
        Assert.Contains("Recovery backup is unavailable.", outcome.Failure, StringComparison.Ordinal);
        Assert.Contains("contact your administrator", outcome.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("reactivate", calls);
        Assert.DoesNotContain("complete", calls);
    }

    [Fact]
    public async Task FailedFilePublicationStillAttemptsFileRecoveryWithoutChangingSettings()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            PublishFiles = () => { calls.Add("publish"); throw new IOException("Connection file could not be published."); },
        });
        Assert.False(outcome.Committed);
        Assert.Equal("Connection file could not be published.", outcome.Failure);
        Assert.Equal(["publish", "restore files"], calls);
    }

    [Fact]
    public async Task StartupFailureIsRecoveredBeforeSettingsAreWritten()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            ChangeStartup = () => Fail(calls, "startup", "Startup task could not be updated."),
        });
        Assert.False(outcome.Committed);
        Assert.Equal(["publish", "startup", "restore startup", "restore files"], calls);
    }

    [Theory]
    [InlineData("host.operation_in_progress", false)]
    [InlineData("host.response_timeout", false)]
    [InlineData("host.connection_lost", true)]
    public async Task UnconfirmedMountDoesNotUndoCommittedConnectionOrRetryStart(string code, bool throws)
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            StartMount = () =>
            {
                calls.Add("start");
                return throws
                    ? Task.FromException<OperationResult>(new IOException("Mount acknowledgement was lost."))
                    : Task.FromResult(Result.Failure(code, "Mount request was not confirmed."));
            },
        });

        Assert.True(outcome.Committed);
        Assert.Null(outcome.Failure);
        Assert.Contains("connection was saved", outcome.MountWarning, StringComparison.Ordinal);
        Assert.Contains("Check the drive status", outcome.MountWarning, StringComparison.Ordinal);
        Assert.Equal(["publish", "startup", "save", "activate", "complete", "start"], calls);
    }

    [Fact]
    public async Task InitialSetupCommitsWithoutHostOrStartupOperationsWhenTheyAreNotRequested()
    {
        var calls = new List<string>();
        var outcome = await SetupCommitTransaction.ApplyAsync(Operations(calls) with
        {
            ChangeStartup = null, RestoreStartup = null, ActivateSettings = null,
            ReactivateSettings = null, StartMount = null,
        });
        Assert.True(outcome.Committed);
        Assert.Null(outcome.MountWarning);
        Assert.Equal(["publish", "save", "complete"], calls);
    }

    private static SetupCommitOperations Operations(List<string> calls) => new(
        () => calls.Add("publish"),
        () => Success(calls, "startup"),
        () => Success(calls, "save"),
        () => Success(calls, "activate"),
        () => calls.Add("complete"),
        () => Success(calls, "restore settings"),
        () => Success(calls, "restore startup"),
        () => calls.Add("restore files"),
        () => Success(calls, "reactivate"),
        () => Success(calls, "start"));

    private static Task<OperationResult> Success(List<string> calls, string operation)
    {
        calls.Add(operation);
        return Task.FromResult(Result.Success());
    }

    private static Task<OperationResult> Fail(List<string> calls, string operation, string message)
    {
        calls.Add(operation);
        return Task.FromResult(Result.Failure("setup.test_failure", message));
    }
}
