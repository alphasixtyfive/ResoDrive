using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResoDrive.App.Tests;

public sealed class ApplicationUpdateHandoffTests
{
    [Theory]
    [InlineData(1601, "IT administrator")]
    [InlineData(1618, "Let it finish")]
    [InlineData(1625, "policy")]
    [InlineData(1632, "disk space")]
    public async Task InstallerEnvironmentFailureExplainsRecoveryAndReopensTheOldApplication(int code, string recovery)
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime { InstallerExitCode = code, ReadyAcknowledged = true };
        Assert.Equal(1, await ApplicationUpdateHandoff.CompleteAsync(request, runtime));
        var outcome = ReadOutcome(request.OutcomePath);
        Assert.Equal("failed", outcome.Status);
        Assert.Equal(code, outcome.InstallerExitCode);
        Assert.Contains(recovery, outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(outcome.Finalized);
        Assert.True(outcome.RelaunchAcknowledged);
        Assert.Equal(request.SourceExecutablePath, runtime.StartedPath);
    }

    [Fact]
    public void FinalReceiptLeftByLegacyHelperIsRecoveredAndRemovedWithTheFirstReceipt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        var first = new ApplicationUpdateOutcome("0.3.19", "succeeded", 0, "Installed.", false, false, DateTimeOffset.UtcNow);
        var final = first with { RelaunchAcknowledged = true, Finalized = true, RecordedAtUtc = first.RecordedAtUtc.AddSeconds(1) };
        File.WriteAllText(path, JsonSerializer.Serialize(first));
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(final));

        Assert.Equal(final, ApplicationUpdateHandoff.ReadOutcome(directory.Path));
        ApplicationUpdateHandoff.DeleteOutcome(directory.Path);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleOrDifferentVersionStagedReceiptCannotReplaceCurrentOutcome(bool differentVersion)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        var first = new ApplicationUpdateOutcome("0.3.19", "succeeded", 0, "Installed.", false, false, DateTimeOffset.UtcNow);
        var staged = first with
        {
            Version = differentVersion ? "0.3.18" : first.Version,
            Finalized = true, RelaunchAcknowledged = true,
            RecordedAtUtc = differentVersion ? first.RecordedAtUtc.AddSeconds(1) : first.RecordedAtUtc.AddSeconds(-1)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(first));
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(staged));
        Assert.Equal(first, ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Fact]
    public void UnreadableStagedReceiptPreservesReadablePrimaryOutcome()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        var first = new ApplicationUpdateOutcome("0.3.19", "succeeded", 0, "Installed.", false, false, DateTimeOffset.UtcNow);
        File.WriteAllText(path, JsonSerializer.Serialize(first));
        File.WriteAllText(path + ".tmp", "incomplete json");
        Assert.Equal(first, ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Fact]
    public void OversizedDiagnosticReceiptIsIgnored()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName), new string('x', 128 * 1024));
        Assert.Null(ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Finalized\":true}")]
    [InlineData("{\"Version\":\"0.3.19\",\"Status\":null,\"Message\":\"Installed.\",\"RecordedAtUtc\":\"2026-10-03T12:00:00Z\"}")]
    public void IncompleteReceiptsCannotBePresentedAsAnUpdateResult(string json)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        File.WriteAllText(path, json);
        File.WriteAllText(path + ".tmp", json);
        Assert.Null(ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreadablePrimaryCannotBeReplacedByAnUnrelatedStagedResult(bool locked)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        File.WriteAllText(path, "incomplete json");
        var staged = new ApplicationUpdateOutcome("0.3.18", "succeeded", 0, "Installed.", true, true, DateTimeOffset.UtcNow);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(staged));
        using var held = locked ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Assert.Null(ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Fact]
    public void FinalizedOrphanStageIsReadableAfterThePrimaryWasRemoved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        var staged = new ApplicationUpdateOutcome("0.3.19", "succeeded", 0, "Installed.", true, true, DateTimeOffset.UtcNow);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(staged));
        Assert.Equal(staged, ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Fact]
    public void PendingReceiptRemainsAvailableToReportAnInterruptedHandoff()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApplicationUpdateHandoff.OutcomeFileName);
        var pending = new ApplicationUpdateOutcome("0.3.19", "pending", null, "Preparing the update.", false, false, DateTimeOffset.UtcNow);
        File.WriteAllText(path, JsonSerializer.Serialize(pending));
        Assert.Equal(pending, ApplicationUpdateHandoff.ReadOutcome(directory.Path));
    }

    [Fact]
    public void PreparationFailureIsShownOnlyForTheCurrentAttempt()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var path = Path.Combine(directory.Path, InstallerPreparation.ResultFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new InstallerPreparationResult(false, "Uploads pending.", now)));
        Assert.Equal("Uploads pending.", ApplicationUpdateHandoff.ReadPreparationFailure(directory.Path, now.AddSeconds(-1)));
        Assert.Null(ApplicationUpdateHandoff.ReadPreparationFailure(directory.Path, now.AddSeconds(1)));
        File.WriteAllText(path, "not json");
        Assert.Null(ApplicationUpdateHandoff.ReadPreparationFailure(directory.Path, now.AddSeconds(-1)));
    }

    [Fact]
    public async Task CompleteAsync_RecordsUacCancellationAndRestoresReadyApplication()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime
        {
            InstallerException = new Win32Exception(1223),
            ReadyAcknowledged = true,
        };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(2, exitCode);
        Assert.Equal("canceled", outcome.Status);
        Assert.Null(outcome.InstallerExitCode);
        Assert.True(outcome.RelaunchAcknowledged);
        Assert.True(outcome.Finalized);
        Assert.Contains("permission was canceled", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(runtime.ApplicationStarted);
        Assert.Equal(request.SourceExecutablePath, runtime.StartedPath);
    }

    [Theory]
    [InlineData(".msi", ".msi.log")]
    [InlineData("-setup.exe", ".setup.log")]
    public async Task CompleteAsync_RecordsInstallerFailureAndRelaunchesApplication(string installerSuffix, string logSuffix)
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path) with
        {
            InstallerPath = Path.Combine(directory.Path, "update" + installerSuffix),
        };
        var runtime = new FakeRuntime { InstallerExitCode = 1603, ReadyAcknowledged = true };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(1, exitCode);
        Assert.Equal("failed", outcome.Status);
        Assert.Equal(1603, outcome.InstallerExitCode);
        Assert.True(outcome.RelaunchAcknowledged);
        Assert.True(outcome.Finalized);
        Assert.Contains("1603", outcome.Message, StringComparison.Ordinal);
        var actualLog = Path.ChangeExtension(request.InstallerPath, logSuffix);
        Assert.Contains(actualLog, outcome.Message, StringComparison.Ordinal);
        Assert.Contains('"' + actualLog + '"', ApplicationUpdateHandoff.CreateInstallerStartInfo(request.InstallerPath).Arguments,
            StringComparison.Ordinal);
        Assert.Equal(request.SourceExecutablePath, runtime.StartedPath);
    }

    [Fact]
    public async Task CompleteAsync_RequiresAcknowledgedReadinessForSuccess()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime { InstallerExitCode = 0, ReadyAcknowledged = false };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(1, exitCode);
        Assert.Equal("succeeded", outcome.Status);
        Assert.False(outcome.RelaunchAcknowledged);
        Assert.True(outcome.Finalized);
        Assert.Contains("did not confirm", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_AcceptsRestartRequiredAndRecordsReadiness()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime { InstallerExitCode = 3010, ReadyAcknowledged = true };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(0, exitCode);
        Assert.Equal("succeeded", outcome.Status);
        Assert.Equal(3010, outcome.InstallerExitCode);
        Assert.True(outcome.RelaunchAcknowledged);
        Assert.True(outcome.Finalized);
        Assert.Contains("restarted", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(request.InstalledExecutablePath, runtime.StartedPath);
    }

    [Fact]
    public async Task CompleteAsync_FallsBackToSourceWhenInstalledApplicationCannotStart()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime
        {
            InstallerExitCode = 0,
            ReadyAcknowledged = true,
            FirstStartException = new Win32Exception(2),
        };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [request.InstalledExecutablePath, request.SourceExecutablePath],
            runtime.StartedPaths);
        Assert.True(ReadOutcome(request.OutcomePath).RelaunchAcknowledged);
    }

    [Fact]
    public async Task CompleteAsync_ReopensVerifiedMovedInstallationAndDoesNotUsePortableFallback()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var moved = Path.Combine(directory.Path, "ResoDrive", "resodrive.exe");
        var runtime = new FakeRuntime { InstallerExitCode = 0, ResolvedExecutable = moved, ReadyAcknowledged = true };
        Assert.Equal(0, await ApplicationUpdateHandoff.CompleteAsync(request, runtime));
        Assert.Equal([moved], runtime.StartedPaths);

        var failedStart = new FakeRuntime { InstallerExitCode = 0, ResolvedExecutable = moved,
            FirstStartException = new Win32Exception(2), ReadyAcknowledged = true };
        Assert.Equal(1, await ApplicationUpdateHandoff.CompleteAsync(request, failedStart));
        Assert.Equal([moved], failedStart.StartedPaths);
        Assert.False(ReadOutcome(request.OutcomePath).RelaunchAcknowledged);
    }

    [Fact]
    public async Task CompleteAsync_PreparesSourceAndInstalledLocationsBeforeStartingInstaller()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime { ReadyAcknowledged = true };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);

        Assert.Equal(0, exitCode);
        Assert.Equal(["wait-parent", "prepare", "prepare", "installer", "relaunch"], runtime.Events);
        Assert.Equal(
            [Path.GetDirectoryName(request.SourceExecutablePath)!,
                Path.GetDirectoryName(request.InstalledExecutablePath)!],
            runtime.PreparedDirectories);
    }

    [Fact]
    public async Task CompleteAsync_PreparationFailureNeverStartsElevatedInstaller()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime
        {
            PreparationException = new IOException("Pending uploads could not be verified."),
            ReadyAcknowledged = true,
        };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(1, exitCode);
        Assert.Equal("failed", outcome.Status);
        Assert.Null(outcome.InstallerExitCode);
        Assert.Contains("Pending uploads", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["wait-parent", "prepare", "relaunch"], runtime.Events);
        Assert.Equal(request.SourceExecutablePath, runtime.StartedPath);
    }

    [Fact]
    public async Task CompleteAsync_PreparationTimeoutRestoresApplicationWithoutStartingInstaller()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime
        {
            PreparationException = new TimeoutException("The host did not stop in time."),
            ReadyAcknowledged = true,
        };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);

        Assert.Equal(1, exitCode);
        Assert.Null(ReadOutcome(request.OutcomePath).InstallerExitCode);
        Assert.Equal(["wait-parent", "prepare", "relaunch"], runtime.Events);
    }

    [Fact]
    public void TryParseCompletionRequest_RejectsMalformedOrUnsafeArguments()
    {
        using var directory = new TemporaryDirectory();
        var updates = Path.Combine(directory.Path, "updates");
        var helper = Path.Combine(updates, "resodrive-update-helper.exe");
        var installed = Path.Combine(
            directory.Path,
            "installed",
            typeof(Program).Assembly.GetName().Name + ".exe");
        var valid = new[]
        {
            ApplicationUpdateHandoff.CompleteArgument,
            "0.3.0",
            Path.Combine(updates, "resodrive-win-x64-0.3.0.msi"),
            Path.Combine(directory.Path, "portable", "resodrive.exe"),
            installed,
            Path.Combine(updates, ApplicationUpdateHandoff.OutcomeFileName),
            new string('A', 64),
            "42",
        };

        Assert.True(ApplicationUpdateHandoff.TryParseCompletionRequest(
            valid, helper, updates, out _));
        var setup = Replace(valid, 2, Path.Combine(updates, "resodrive-win-x64-0.3.0-setup.exe"));
        Assert.True(ApplicationUpdateHandoff.TryParseCompletionRequest(
            setup, helper, updates, out _));
        Assert.False(ApplicationUpdateHandoff.TryParseCompletionRequest(
            Replace(setup, 2, Path.Combine(updates, "resodrive-win-x64-0.3.18-setup.exe")), helper, updates, out _));
        Assert.False(ApplicationUpdateHandoff.TryParseCompletionRequest(
            valid, Path.Combine(directory.Path, "installed", "resodrive.exe"), updates, out _));
        Assert.False(ApplicationUpdateHandoff.TryParseCompletionRequest(
            Replace(valid, 2, Path.Combine(directory.Path, "attacker.msi")), helper, updates, out _));
        Assert.False(ApplicationUpdateHandoff.TryParseCompletionRequest(
            Replace(valid, 3, Path.Combine(updates, "resodrive.exe")), helper, updates, out _));
        Assert.False(ApplicationUpdateHandoff.TryParseCompletionRequest(
            Replace(valid, 5, Path.Combine(directory.Path, "result.json")), helper, updates, out _));
    }

    [Fact]
    public void CreateInstallerStartInfo_UsesPassiveNoRestartInstallWithDurableLog()
    {
        var installerPath = Path.Combine("C:\\updates", "resodrive-win-x64-0.3.0.msi");

        var startInfo = ApplicationUpdateHandoff.CreateInstallerStartInfo(installerPath);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), startInfo.FileName);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Contains("/passive", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/norestart", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/l*v", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resodrive-win-x64-0.3.0.msi.log", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RDRIVE_DATA_ROOT=", startInfo.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupHandoffUsesTheBrandedBundleWithExplicitDataRoot()
    {
        var path = @"C:\updates\resodrive-win-x64-0.3.36-setup.exe";
        var start = ApplicationUpdateHandoff.CreateInstallerStartInfo(path);
        Assert.Equal(path, start.FileName);
        Assert.Equal("runas", start.Verb);
        Assert.Contains("/passive /norestart /log", start.Arguments, StringComparison.Ordinal);
        Assert.Contains("ResoDriveDataRoot=", start.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("/i ", start.Arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1618)]
    [InlineData(unchecked((int)0x80070652))]
    public void BusyInstallerResultIsActionableForMsiAndSetup(int code)
    {
        var result = ApplicationUpdateHandoff.ClassifyInstallerExitCode(code);
        Assert.Equal("failed", result.Status);
        Assert.Contains("Let it finish", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_RejectsInstallerOutsideTrustedUpdatesDirectory()
    {
        using var directory = new TemporaryDirectory();
        var updates = Path.Combine(directory.Path, "updates");
        var executable = Path.Combine(
            directory.Path,
            typeof(Program).Assembly.GetName().Name + ".exe");

        Assert.Throws<InvalidOperationException>(() => ApplicationUpdateHandoff.Start(
            "0.3.0",
            Path.Combine(directory.Path, "attacker.msi"),
            updates,
            executable,
            Path.Combine(directory.Path, "installed", "resodrive.exe"),
            new string('A', 64)));
    }

    [Fact]
    public async Task VerifiedInstaller_IsRehashedAndLockedAgainstReplacement()
    {
        using var directory = new TemporaryDirectory();
        var installer = Path.Combine(directory.Path, "update.msi");
        await File.WriteAllTextAsync(installer, "verified installer");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified installer")));

        await using (var held = await ApplicationUpdateHandoff.OpenVerifiedInstallerAsync(
                         installer,
                         expected))
        {
            Assert.NotNull(held);
            Assert.Throws<IOException>(() => File.WriteAllText(installer, "replacement"));
        }

        await File.WriteAllTextAsync(installer, "replacement");
        Assert.Null(await ApplicationUpdateHandoff.OpenVerifiedInstallerAsync(installer, expected));
    }

    private static string[] Replace(string[] values, int index, string replacement)
    {
        var copy = (string[])values.Clone();
        copy[index] = replacement;
        return copy;
    }

    private static ApplicationUpdateCompletionRequest Request(string directory) => new(
        "0.3.0",
        Path.Combine(directory, "update.msi"),
        Path.Combine(directory, "portable", "resodrive.exe"),
        Path.Combine(directory, "installed", "resodrive.exe"),
        Path.Combine(directory, ApplicationUpdateHandoff.OutcomeFileName),
        new string('A', 64),
        42);

    private static ApplicationUpdateOutcome ReadOutcome(string path) =>
        JsonSerializer.Deserialize<ApplicationUpdateOutcome>(File.ReadAllText(path))!;

    private sealed class FakeRuntime : IApplicationUpdateRuntime
    {
        public int InstallerExitCode { get; init; }
        public Exception? InstallerException { get; init; }
        public Exception? PreparationException { get; init; }
        public bool ReadyAcknowledged { get; init; }
        public Exception? FirstStartException { get; init; }
        public string? ResolvedExecutable { get; init; }
        public bool ApplicationStarted { get; private set; }
        public string? StartedPath { get; private set; }
        public List<string> PreparedDirectories { get; } = [];
        public List<string> StartedPaths { get; } = [];
        public List<string> Events { get; } = [];

        public Task WaitForParentExitAsync(int processId, CancellationToken cancellationToken)
        {
            Events.Add("wait-parent");
            return Task.CompletedTask;
        }

        public Task PrepareForInstallerAsync(string installationDirectory, CancellationToken cancellationToken)
        {
            Events.Add("prepare");
            PreparedDirectories.Add(installationDirectory);
            return PreparationException is null
                ? Task.CompletedTask
                : Task.FromException(PreparationException);
        }

        public Task<int> RunInstallerAsync(
            string installerPath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            Events.Add("installer");
            return InstallerException is null
                ? Task.FromResult(InstallerExitCode)
                : Task.FromException<int>(InstallerException);
        }

        public bool StartApplication(string executablePath)
        {
            Events.Add("relaunch");
            ApplicationStarted = true;
            StartedPath = executablePath;
            StartedPaths.Add(executablePath);
            if (StartedPaths.Count == 1 && FirstStartException is not null)
                throw FirstStartException;
            return true;
        }

        public bool RequestReady(string activationScope, TimeSpan timeout) => ReadyAcknowledged;
        public string? ResolveInstalledApplication(string version) => ResolvedExecutable;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "resodrive-update-handoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
