using System.Reflection;
using System.Windows;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class MainWindowStartupTests
{
    // Use the shared STA/Application fixture without showing the window or starting a host.
    internal static void VerifyWithApplicationResources()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "resodrive-startup-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.Rclone);
        File.WriteAllText(paths.RcloneExecutable, "Disposable engine placeholder");
        var runner = new VersionRunner();
        var locator = new RcloneRuntimeLocator(paths, runner);
        var window = new MainWindow(false, locator);
        window.Loaded -= Method("MainWindow_Loaded").CreateDelegate<RoutedEventHandler>(window);
        try
        {
            var observation = locator.InspectAsync().GetAwaiter().GetResult();
            var generation = Generation(window);
            Refresh(window, observation, generation);
            Assert.Equal(1, runner.Calls); // Initial inspection supplies the component row too.
            Assert.Equal("v1.75.1", InstalledVersion(window));

            observation = locator.InspectAsync().GetAwaiter().GetResult();
            generation = Generation(window);
            Method("SetRcloneUpdateBusy").Invoke(window, [true, null, true]);
            Method("SetRcloneUpdateBusy").Invoke(window, [false, null, false]);
            runner.Version = "v1.76.0";
            Refresh(window, observation, generation);
            Assert.Equal(3, runner.Calls); // A completed mutation invalidates the earlier result.
            Assert.Equal("v1.76.0", InstalledVersion(window));

            // Setup discards its pre-dialog observation. Ordinary refreshes carry no result either.
            runner.Version = "v1.77.0";
            Refresh(window, null, Generation(window));
            Refresh(window, null, null);
            Assert.Equal(5, runner.Calls);
            Assert.Equal("v1.77.0", InstalledVersion(window));

            var failure = Result.Failure<InstallationStatus>("rclone.version_timeout", "Timed out", true);
            Refresh(window, failure, Generation(window));
            Assert.Equal(6, runner.Calls); // A failed early check does not suppress a fresh attempt.
            Assert.Equal("v1.77.0", InstalledVersion(window));

            runner.WaitForCancellation = true;
            var pending = (Task)Method("UpdateLocalRcloneStatusAsync").Invoke(window, [null, null])!;
            Assert.True(runner.LastToken.CanBeCanceled);
            Close(window);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.True(pending.IsCanceled, "Closing must cancel a fresh component inspection.");
            Assert.True(runner.LastToken.IsCancellationRequested);
        }
        finally
        {
            Close(window);
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static void Refresh(MainWindow window, OperationResult<InstallationStatus>? result, int? generation) =>
        Method("UpdateConnectionStatus").Invoke(window, [result, generation]);

    private static int Generation(MainWindow window) => (int)Field("_componentStatusGeneration").GetValue(window)!;
    private static string? InstalledVersion(MainWindow window) => (string?)Field("_rcloneInstalledVersion").GetValue(window);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Close(MainWindow window)
    {
        if (window.IsClosing) return;
        Field("_exitRequested").SetValue(window, true);
        window.Close();
    }

    private sealed class VersionRunner : IRcloneProcessRunner
    {
        internal string Version { get; set; } = "v1.75.1";
        internal int Calls { get; private set; }
        internal bool WaitForCancellation { get; set; }
        internal CancellationToken LastToken { get; private set; }

        public async Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default, Action<string>? onOutputLine = null)
        {
            Calls++;
            LastToken = cancellationToken;
            Assert.Equal("version", Assert.Single(arguments));
            if (WaitForCancellation)
            {
                Assert.True(cancellationToken.CanBeCanceled);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            return new(0, $"rclone {Version}\n", string.Empty, false);
        }
    }
}
