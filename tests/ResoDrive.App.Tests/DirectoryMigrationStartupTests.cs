using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ResoDrive.App.Tests;

public sealed class DirectoryMigrationStartupTests
{
    [Fact]
    public void FreshlyDiscoveredHelperSkipsOtherSessionsAndVerifiesItsRealImageAndAccount()
    {
        using var child = StartDisposableProcess();
        try
        {
            var expectedPath = child.MainModule!.FileName;
            var expectedSession = child.SessionId;
            // Unlike Process.Start's object, a discovered object initially has no
            // retained process handle. Exercise the actual acquisition boundary.
            using var discovered = Process.GetProcessById(child.Id);
            var imageRead = false;
            Assert.False(DirectoryMigrationStartup.IsLegacyHelper(discovered, expectedPath,
                expectedSession + 1, _ =>
                {
                    imageRead = true;
                    throw new InvalidOperationException("A different-session helper must not be inspected.");
                }));
            Assert.False(imageRead);
            Assert.False(child.HasExited);

            Assert.True(DirectoryMigrationStartup.IsLegacyHelper(discovered, expectedPath, expectedSession));
            Assert.False(discovered.SafeHandle.IsInvalid);
            Assert.Equal(child.StartTime, discovered.StartTime);
            Assert.False(child.HasExited);
        }
        finally { StopDisposableProcess(child); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingImageIsIgnoredOnlyIfTheSameHelperExitedDuringInspection(bool exitDuringInspection)
    {
        using var process = StartDisposableProcess();
        try
        {
            var path = process.MainModule!.FileName;
            var sessionId = process.SessionId;
            bool Inspect() => DirectoryMigrationStartup.IsLegacyHelper(process, path, sessionId, candidate =>
            {
                Assert.Same(process, candidate);
                Assert.False(candidate.HasExited);
                if (exitDuringInspection)
                {
                    candidate.Kill(entireProcessTree: true);
                    Assert.True(candidate.WaitForExit(5000));
                }
                return null;
            });
            if (exitDuringInspection) Assert.False(Inspect());
            else
            {
                var failure = Assert.Throws<IOException>(() => Inspect());
                Assert.Contains("executable identity could not be verified", failure.Message, StringComparison.Ordinal);
                Assert.False(process.HasExited);
            }
        }
        finally { StopDisposableProcess(process); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitDuringImageInspectionSkipsOnlyTheSameExitedProcess(bool win32Failure)
    {
        using var process = StartDisposableProcess();
        try
        {
            var path = process.MainModule!.FileName;
            var sessionId = process.SessionId;
            var inspected = false;
            Assert.False(DirectoryMigrationStartup.IsLegacyHelper(process, path, sessionId, candidate =>
            {
                Assert.Same(process, candidate);
                Assert.False(candidate.HasExited);
                inspected = true;
                candidate.Kill(entireProcessTree: true);
                Assert.True(candidate.WaitForExit(5000));
                throw win32Failure
                    ? new Win32Exception(299, "Only part of a ReadProcessMemory request was completed.")
                    : new InvalidOperationException("The process exited during inspection.");
            }));
            Assert.True(inspected);
            Assert.True(process.HasExited);
        }
        finally { StopDisposableProcess(process); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableLiveHelperStillBlocksMigration(bool win32Failure)
    {
        using var process = StartDisposableProcess();
        try
        {
            var failure = win32Failure
                ? (Exception)new Win32Exception(299, "Only part of a ReadProcessMemory request was completed.")
                : new InvalidOperationException("The live image is unavailable.");
            var elapsed = Stopwatch.StartNew();
            var observed = Assert.ThrowsAny<Exception>(() => DirectoryMigrationStartup.IsLegacyHelper(
                process, process.MainModule!.FileName, process.SessionId, candidate =>
                {
                    Assert.Same(process, candidate);
                    Assert.False(candidate.HasExited);
                    throw failure;
                }));
            Assert.Same(failure, observed);
            elapsed.Stop();
            if (win32Failure) Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(900),
                $"A live partial-copy fault did not use its bounded kernel exit wait: {elapsed.Elapsed}.");
            Assert.False(process.HasExited);
        }
        finally { StopDisposableProcess(process); }
    }

    [Fact]
    public void LiveCandidateStillRequiresItsRealImageAndCurrentAccountSession()
    {
        using var process = StartDisposableProcess();
        try
        {
            var path = process.MainModule!.FileName;
            Assert.True(DirectoryMigrationStartup.IsLegacyHelper(process, path, process.SessionId));
            Assert.False(DirectoryMigrationStartup.IsLegacyHelper(process,
                Path.Combine(Path.GetDirectoryName(path)!, "different-helper.exe"), process.SessionId));
            Assert.False(DirectoryMigrationStartup.IsLegacyHelper(process, path, process.SessionId + 1));
            Assert.Throws<IOException>(() => DirectoryMigrationStartup.IsLegacyHelper(
                process, path, process.SessionId, _ => null));
            Assert.False(process.HasExited);
        }
        finally { StopDisposableProcess(process); }
    }

    [Fact]
    public void PartialCopyWhileHelperIsExitingWaitsForItsRetainedKernelHandle()
    {
        using var process = StartDisposableProcess();
        try
        {
            var inspected = false;
            var exitWaitEntered = false;
            Assert.False(DirectoryMigrationStartup.IsLegacyHelper(process, process.MainModule!.FileName,
                process.SessionId, candidate =>
                {
                    inspected = true;
                    Assert.False(candidate.HasExited);
                    throw new Win32Exception(299, "Only part of a ReadProcessMemory request was completed.");
                }, (candidate, milliseconds) =>
                {
                    Assert.Same(process, candidate);
                    Assert.False(candidate.HasExited); // Deterministically reach the kernel-wait boundary while live.
                    Assert.Equal(1000, milliseconds);
                    exitWaitEntered = true;
                    candidate.StandardInput.WriteLine("exit");
                    candidate.StandardInput.Flush();
                    return candidate.WaitForExit(milliseconds);
                }));
            Assert.True(inspected);
            Assert.True(exitWaitEntered);
            Assert.True(process.HasExited);
        }
        finally { StopDisposableProcess(process); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnauthorizedInspectionIsIgnoredOnlyForAConfirmedExitedHelper(bool exitDuringInspection)
    {
        using var process = StartDisposableProcess();
        try
        {
            var failure = new UnauthorizedAccessException("The helper account identity could not be read.");
            var exitWaitEntered = false;
            bool Inspect() => DirectoryMigrationStartup.IsLegacyHelper(process, process.MainModule!.FileName,
                process.SessionId, candidate =>
                {
                    if (exitDuringInspection)
                    {
                        candidate.Kill(entireProcessTree: true);
                        Assert.True(candidate.WaitForExit(5000));
                    }
                    throw failure;
                }, (candidate, milliseconds) =>
                {
                    exitWaitEntered = true;
                    return candidate.WaitForExit(milliseconds);
                });
            if (exitDuringInspection) Assert.False(Inspect());
            else
            {
                Assert.Same(failure, Assert.Throws<UnauthorizedAccessException>(() => Inspect()));
                Assert.False(process.HasExited);
            }
            Assert.False(exitWaitEntered);
        }
        finally { StopDisposableProcess(process); }
    }

    [Fact]
    public void AccessDeniedForLiveHelperDoesNotWaitForThePartialCopyExitBound()
    {
        using var process = StartDisposableProcess();
        try
        {
            var failure = new Win32Exception(5, "Access is denied.");
            var exitWaitEntered = false;
            var observed = Assert.Throws<Win32Exception>(() => DirectoryMigrationStartup.IsLegacyHelper(
                process, process.MainModule!.FileName, process.SessionId, _ => throw failure,
                (candidate, milliseconds) =>
                {
                    exitWaitEntered = true;
                    return candidate.WaitForExit(milliseconds);
                }));
            Assert.Same(failure, observed);
            Assert.False(process.HasExited);
            Assert.False(exitWaitEntered);
        }
        finally { StopDisposableProcess(process); }
    }

    private static Process StartDisposableProcess()
    {
        var process = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            "/d /q /k \"echo resodrive-boundary-ready\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("The disposable test process could not be started.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("resodrive-boundary-ready",
                process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult());
            Assert.False(process.HasExited);
            return process;
        }
        catch
        {
            StopDisposableProcess(process);
            process.Dispose();
            throw;
        }
    }

    private static void StopDisposableProcess(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(5000));
    }
}
