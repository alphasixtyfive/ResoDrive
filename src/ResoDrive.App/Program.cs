using ResoDrive.Host;
using ResoDrive.Windows;
using System.Diagnostics;
using System.IO;

namespace ResoDrive.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ApplicationUpdateHandoff.TryParseCompletionRequest(args, out var completionRequest))
        {
            return ApplicationUpdateHandoff.CompleteAsync(completionRequest)
                .GetAwaiter().GetResult();
        }
        if (args.Length > 0 &&
            args[0].Equals(ApplicationUpdateHandoff.CompleteArgument, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (args.Length == 1 &&
            args[0].Equals("--prepare-update", StringComparison.OrdinalIgnoreCase))
        {
            return PrepareForUpdateAsync().GetAwaiter().GetResult();
        }

        if (args.Length == 1 &&
            args[0].Equals("--unregister-autostart", StringComparison.OrdinalIgnoreCase))
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                return 1;
            var result = new ScheduledTaskAutostartService(executablePath)
                .SetEnabledAsync(false)
                .GetAwaiter().GetResult();
            return result.Succeeded ? 0 : 1;
        }

        if (args.Any(argument => argument.Equals("--host", StringComparison.OrdinalIgnoreCase)))
        {
            HostApplication.RunAsync(args).GetAwaiter().GetResult();
            return 0;
        }

        if (args.Length == 1 && args[0].Equals("password", StringComparison.OrdinalIgnoreCase))
        {
            var password = new DpapiSecretStore(new ApplicationPaths())
                .LoadAsync()
                .GetAwaiter()
                .GetResult();
            Console.Out.WriteLine(password);
            return 0;
        }

        if (args.Length == 2 && args[0].Equals("password-file", StringComparison.OrdinalIgnoreCase))
        {
            var paths = new ApplicationPaths();
            var candidate = Path.GetFullPath(args[1]);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !candidate.EndsWith(".setup-secret", StringComparison.Ordinal) ||
                !File.Exists(candidate) ||
                File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
                return 2;
            var password = new DpapiSecretStore(paths)
                .LoadProtectedFileAsync(candidate)
                .GetAwaiter()
                .GetResult();
            Console.Out.WriteLine(password);
            return 0;
        }

        try
        {
            var application = new App();
            application.InitializeComponent();
            return application.Run();
        }
        catch (Exception exception)
        {
            var errorId = UiDiagnosticLog.Current.Exception("startup.fatal", exception);
            try
            {
                System.Windows.MessageBox.Show(
                    $"{ProductInfo.Name} could not start.\n\n{exception.Message}\n\nError ID: {errorId}",
                    $"{ProductInfo.Name} startup error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch (Exception dialogException)
            {
                UiDiagnosticLog.Current.Exception("startup.error_dialog_failed", dialogException);
            }
            return 1;
        }
    }

    internal static void TryStartHostEarly()
    {
        try
        {
            var paths = new ApplicationPaths();
            var executablePath = Environment.ProcessPath;
            if (!File.Exists(paths.SettingsFile) || string.IsNullOrWhiteSpace(executablePath))
                return;

            using var process = Process.Start(new ProcessStartInfo(executablePath, "--host")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            // MainWindow.EnsureHostAsync performs the observable retry and reports failure.
        }
    }

    private static async Task<int> PrepareForUpdateAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await HostClient.SendAsync(
                new HostRequest("shutdown", Confirmed: true),
                timeout.Token).ConfigureAwait(false);
            if (response.ErrorCode is "host.unavailable" or "host.different_installation")
                return 0;
            if (!response.Succeeded)
                return 1;

            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false);
                var status = await HostClient.SendAsync(
                    new HostRequest("status"),
                    TimeSpan.FromSeconds(1),
                    timeout.Token).ConfigureAwait(false);
                if (status.ErrorCode == "host.unavailable")
                    return 0;
                if (!status.Succeeded && status.ErrorCode != "host.response_timeout")
                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 1;
        }
    }
}
