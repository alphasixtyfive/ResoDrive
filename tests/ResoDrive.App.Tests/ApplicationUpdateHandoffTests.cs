using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResoDrive.App.Tests;

public sealed class ApplicationUpdateHandoffTests
{
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

    [Fact]
    public async Task CompleteAsync_RecordsInstallerFailureAndRelaunchesApplication()
    {
        using var directory = new TemporaryDirectory();
        var request = Request(directory.Path);
        var runtime = new FakeRuntime { InstallerExitCode = 1603, ReadyAcknowledged = true };

        var exitCode = await ApplicationUpdateHandoff.CompleteAsync(request, runtime);
        var outcome = ReadOutcome(request.OutcomePath);

        Assert.Equal(1, exitCode);
        Assert.Equal("failed", outcome.Status);
        Assert.Equal(1603, outcome.InstallerExitCode);
        Assert.True(outcome.RelaunchAcknowledged);
        Assert.True(outcome.Finalized);
        Assert.Contains("1603", outcome.Message, StringComparison.Ordinal);
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

        Assert.Equal("msiexec.exe", startInfo.FileName);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Contains("/passive", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/norestart", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/l*v", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resodrive-win-x64-0.3.0.msi.log", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RDRIVE_DATA_ROOT=", startInfo.Arguments, StringComparison.Ordinal);
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
        public bool ReadyAcknowledged { get; init; }
        public Exception? FirstStartException { get; init; }
        public bool ApplicationStarted { get; private set; }
        public string? StartedPath { get; private set; }
        public List<string> StartedPaths { get; } = [];

        public Task WaitForParentExitAsync(int processId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> RunInstallerAsync(
            string installerPath,
            string expectedSha256,
            CancellationToken cancellationToken) =>
            InstallerException is null
                ? Task.FromResult(InstallerExitCode)
                : Task.FromException<int>(InstallerException);

        public bool StartApplication(string executablePath)
        {
            ApplicationStarted = true;
            StartedPath = executablePath;
            StartedPaths.Add(executablePath);
            if (StartedPaths.Count == 1 && FirstStartException is not null)
                throw FirstStartException;
            return true;
        }

        public bool RequestReady(string activationScope, TimeSpan timeout) => ReadyAcknowledged;
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
