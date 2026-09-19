using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed record ApplicationUpdateCompletionRequest(
    string Version,
    string InstallerPath,
    string SourceExecutablePath,
    string InstalledExecutablePath,
    string OutcomePath,
    string ExpectedSha256,
    int ParentProcessId);

internal sealed record ApplicationUpdateOutcome(
    string Version,
    string Status,
    int? InstallerExitCode,
    string Message,
    bool RelaunchAcknowledged,
    bool Finalized,
    DateTimeOffset RecordedAtUtc);

internal interface IApplicationUpdateRuntime
{
    Task WaitForParentExitAsync(int processId, CancellationToken cancellationToken);
    Task<int> RunInstallerAsync(
        string installerPath,
        string expectedSha256,
        CancellationToken cancellationToken);
    bool StartApplication(string executablePath);
    bool RequestReady(string activationScope, TimeSpan timeout);
}

internal static class ApplicationUpdateHandoff
{
    internal const string CompleteArgument = "--complete-update";
    internal const string OutcomeFileName = "application-update-result.json";
    private const string HandoffDirectoryEnvironmentVariable = "RDRIVE_UPDATE_HANDOFF_DIR";
    private const int ErrorCancelled = 1223;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static void Start(
        string version,
        string installerPath,
        string updatesDirectory,
        string sourceExecutablePath,
        string installedExecutablePath,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var sourceExecutable = Path.GetFullPath(sourceExecutablePath);
        var installedExecutable = Path.GetFullPath(installedExecutablePath);
        var installer = Path.GetFullPath(installerPath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesDirectory));
        var expectedInstaller = Path.Combine(directory, $"resodrive-win-x64-{version}.msi");
        var expectedExecutableName = typeof(Program).Assembly.GetName().Name + ".exe";
        if (!Version.TryParse(version, out var parsedVersion) || parsedVersion.Build < 0 ||
            parsedVersion.Revision >= 0 || parsedVersion.ToString(3) != version ||
            !installer.Equals(expectedInstaller, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(sourceExecutable).Equals(
                expectedExecutableName,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(installedExecutable).Equals(
                expectedExecutableName,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(sourceExecutable)!.Equals(
                directory,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(installedExecutable)!.Equals(
                directory,
                StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(expectedSha256))
        {
            throw new InvalidOperationException("The update handoff paths are invalid.");
        }
        if (!File.Exists(sourceExecutable) || !File.Exists(installer))
            throw new FileNotFoundException("The update handoff files are no longer available.");

        Directory.CreateDirectory(directory);
        var helperPath = Path.Combine(directory, "resodrive-update-helper.exe");
        var outcomePath = Path.Combine(directory, OutcomeFileName);
        File.Copy(sourceExecutable, helperPath, overwrite: true);
        WriteOutcome(outcomePath, new ApplicationUpdateOutcome(
            version,
            "pending",
            null,
            "Waiting for Windows Installer.",
            false,
            false,
            DateTimeOffset.UtcNow));

        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        startInfo.ArgumentList.Add(CompleteArgument);
        startInfo.ArgumentList.Add(version);
        startInfo.ArgumentList.Add(installer);
        startInfo.ArgumentList.Add(sourceExecutable);
        startInfo.ArgumentList.Add(installedExecutable);
        startInfo.ArgumentList.Add(outcomePath);
        startInfo.ArgumentList.Add(expectedSha256.ToUpperInvariant());
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment[HandoffDirectoryEnvironmentVariable] = directory;
        using var helper = Process.Start(startInfo);
        if (helper is null)
            throw new InvalidOperationException("The update handoff process could not be started.");
    }

    internal static bool TryParseCompletionRequest(
        IReadOnlyList<string> arguments,
        out ApplicationUpdateCompletionRequest request) =>
        TryParseCompletionRequest(
            arguments,
            Environment.ProcessPath,
            Environment.GetEnvironmentVariable(HandoffDirectoryEnvironmentVariable) ??
                new ApplicationPaths().Updates,
            out request);

    internal static bool TryParseCompletionRequest(
        IReadOnlyList<string> arguments,
        string? helperExecutablePath,
        string trustedUpdatesDirectory,
        out ApplicationUpdateCompletionRequest request)
    {
        request = null!;
        if (arguments.Count != 8 ||
            !arguments[0].Equals(CompleteArgument, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(arguments[1]) ||
            !IsSha256(arguments[6]) ||
            !int.TryParse(arguments[7], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
            processId <= 0)
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(helperExecutablePath))
                return false;
            var helperPath = Path.GetFullPath(helperExecutablePath);
            var updatesDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(trustedUpdatesDirectory));
            if (!Path.GetFileName(helperPath).Equals(
                    "resodrive-update-helper.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetDirectoryName(helperPath)!.Equals(
                    updatesDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(arguments[1], out var version) ||
                version.Build < 0 || version.Revision >= 0 ||
                version.ToString(3) != arguments[1])
            {
                return false;
            }

            var installerPath = Path.GetFullPath(arguments[2]);
            var sourceExecutablePath = Path.GetFullPath(arguments[3]);
            var installedExecutablePath = Path.GetFullPath(arguments[4]);
            var outcomePath = Path.GetFullPath(arguments[5]);
            var expectedInstallerPath = Path.Combine(
                updatesDirectory,
                $"resodrive-win-x64-{arguments[1]}.msi");
            var expectedOutcomePath = Path.Combine(updatesDirectory, OutcomeFileName);
            var expectedExecutableName = typeof(Program).Assembly.GetName().Name + ".exe";
            if (!installerPath.Equals(expectedInstallerPath, StringComparison.OrdinalIgnoreCase) ||
                !outcomePath.Equals(expectedOutcomePath, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(sourceExecutablePath).Equals(
                    expectedExecutableName,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(installedExecutablePath).Equals(
                    expectedExecutableName,
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(sourceExecutablePath)!.Equals(
                    updatesDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(installedExecutablePath)!.Equals(
                    updatesDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            request = new ApplicationUpdateCompletionRequest(
                arguments[1],
                installerPath,
                sourceExecutablePath,
                installedExecutablePath,
                outcomePath,
                arguments[6].ToUpperInvariant(),
                processId);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static async Task<int> CompleteAsync(
        ApplicationUpdateCompletionRequest request,
        IApplicationUpdateRuntime? runtime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        runtime ??= new ApplicationUpdateRuntime();
        await runtime.WaitForParentExitAsync(request.ParentProcessId, cancellationToken)
            .ConfigureAwait(false);

        var status = "failed";
        var installStartedAtUtc = DateTimeOffset.UtcNow;
        int? installerExitCode = null;
        var message = "Windows Installer did not complete the ResoDrive update.";
        try
        {
            installerExitCode = await runtime.RunInstallerAsync(
                    request.InstallerPath,
                    request.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            (status, message) = ClassifyInstallerExitCode(installerExitCode.Value);
            if (status == "failed")
            {
                var preparation = ReadPreparationFailure(Path.GetDirectoryName(request.InstallerPath)!, installStartedAtUtc);
                if (preparation is not null)
                    message += " " + preparation;
                message += " See the installer log at " +
                    Path.ChangeExtension(request.InstallerPath, ".msi.log") + ".";
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            status = "canceled";
            message = "Windows permission was canceled. ResoDrive was not updated.";
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or InvalidDataException or
                UnauthorizedAccessException)
        {
            message = "Windows Installer could not complete the update: " + exception.Message;
        }

        var outcome = new ApplicationUpdateOutcome(
            request.Version,
            status,
            installerExitCode,
            message,
            false,
            false,
            DateTimeOffset.UtcNow);
        TryWriteOutcome(request.OutcomePath, outcome);

        var acknowledged = false;
        Exception? relaunchException = null;
        var relaunchPaths = status == "succeeded"
            ? new[] { request.InstalledExecutablePath, request.SourceExecutablePath }
            : new[] { request.SourceExecutablePath };
        foreach (var relaunchPath in relaunchPaths)
        {
            try
            {
                if (!runtime.StartApplication(relaunchPath))
                    continue;
                var directory = Path.GetDirectoryName(relaunchPath)
                    ?? throw new InvalidOperationException("The application path is invalid.");
                acknowledged = runtime.RequestReady(App.CreateInstanceScope(directory), TimeSpan.FromMinutes(2));
                if (acknowledged)
                    break;
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                relaunchException = exception;
            }
        }
        if (!acknowledged && relaunchException is not null)
            message += " ResoDrive could not be reopened: " + relaunchException.Message;

        outcome = outcome with
        {
            Message = acknowledged
                ? message
                : message + " ResoDrive did not confirm that its window was ready.",
            RelaunchAcknowledged = acknowledged,
            Finalized = true,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };
        TryWriteOutcome(request.OutcomePath, outcome);
        return status == "succeeded" && acknowledged ? 0 : status == "canceled" ? 2 : 1;
    }

    internal static ApplicationUpdateOutcome? ReadOutcome(string updatesDirectory)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(updatesDirectory), OutcomeFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ApplicationUpdateOutcome>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? ReadPreparationFailure(string updatesDirectory, DateTimeOffset startedAtUtc)
    {
        try
        {
            var path = Path.Combine(updatesDirectory, InstallerPreparation.ResultFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
                return null;
            var result = JsonSerializer.Deserialize<InstallerPreparationResult>(File.ReadAllText(path));
            return result is { Succeeded: false } && result.RecordedAtUtc >= startedAtUtc
                ? result.Message : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void DeleteOutcome(string updatesDirectory)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(updatesDirectory), OutcomeFileName);
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A stale diagnostic result can be retried on the next launch.
        }
    }

    internal static (string Status, string Message) ClassifyInstallerExitCode(int exitCode) =>
        exitCode switch
        {
            0 => ("succeeded", "The ResoDrive update was installed."),
            1641 => ("succeeded", "The ResoDrive update was installed and Windows initiated a restart."),
            3010 => ("succeeded", "The ResoDrive update was installed. Windows should be restarted."),
            1602 => ("canceled", "Windows Installer was canceled. ResoDrive was not updated."),
            _ => ("failed", $"Windows Installer stopped with code {exitCode}. ResoDrive was not updated."),
        };

    private static void WriteOutcome(string path, ApplicationUpdateOutcome outcome)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outcome, JsonOptions));
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static void TryWriteOutcome(string path, ApplicationUpdateOutcome outcome)
    {
        try
        {
            WriteOutcome(path, outcome);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Failure to update the diagnostic record must not prevent the application relaunch.
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath)
    {
        var logPath = Path.ChangeExtension(installerPath, ".msi.log");
        var dataRoot = new ApplicationPaths().Root;
        return new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{installerPath}\" /passive /norestart /l*v \"{logPath}\" RDRIVE_DATA_ROOT=\"{Path.TrimEndingDirectorySeparator(dataRoot)}\\.\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        };
    }

    internal static async Task<FileStream?> OpenVerifiedInstallerAsync(
        string installerPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(expectedSha256))
            return null;
        var stream = new FileStream(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed class ApplicationUpdateRuntime : IApplicationUpdateRuntime
    {
        public async Task WaitForParentExitAsync(int processId, CancellationToken cancellationToken)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The process exited before the helper opened it.
            }
            catch (InvalidOperationException)
            {
                // The process exited while its wait handle was being opened.
            }
            catch (Win32Exception)
            {
                // The installer can still perform the coordinated shutdown itself.
            }
        }

        public async Task<int> RunInstallerAsync(
            string installerPath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            await using var verifiedInstaller = await OpenVerifiedInstallerAsync(
                    installerPath,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException(
                    "The installer changed after download verification.");
            var startInfo = CreateInstallerStartInfo(installerPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public bool StartApplication(string executablePath)
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
            });
            return process is not null;
        }

        public bool RequestReady(string activationScope, TimeSpan timeout) =>
            SingleInstanceActivation.RequestShow(activationScope, timeout);
    }
}
