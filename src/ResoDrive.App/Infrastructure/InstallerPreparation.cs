using System.IO;
using System.Text.Json;
using System.Windows;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed record InstallerPreparationResult(bool Succeeded, string Message, DateTimeOffset RecordedAtUtc);

internal static class InstallerPreparation
{
    internal const string ResultFileName = "installer-preparation.json";

    internal static int Run(string installationDirectory, bool interactive, bool includeRegisteredInstallation = false) =>
        Run(interactive, () => PrepareAsync(installationDirectory, includeRegisteredInstallation), ShowFailure);

    internal static int Run(bool interactive, Func<Task<InstallerPreparationResult>> prepare, Action<string> showFailure)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(showFailure);
        // MSI/Burn owns routine progress. Do not open a competing window just
        // to perform a preparation stage that already has MSI ProgressText.
        var result = prepare().GetAwaiter().GetResult();
        if (result.Succeeded) return 0;
        if (interactive) showFailure(result.Message);
        return 1;
    }

    private static void ShowFailure(string message)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/resodrive;component/Themes/Controls.xaml", UriKind.Relative)
        });
        var dialog = new MessageDialog("Installation needs attention", message, MessageBoxButton.OK, MessageBoxImage.Warning)
        {
            Title = "ResoDrive Setup",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };
        dialog.ShowDialog();
        application.Shutdown(1);
    }

    private static async Task<InstallerPreparationResult> PrepareAsync(string directory, bool includeRegisteredInstallation)
    {
        InstallerPreparationResult result;
        try
        {
            UiDiagnosticLog.Current.Information("installer.prepare_started");
            var directories = (includeRegisteredInstallation ? InstalledApplicationLocator.GetInstallationDirectories() : [])
                .Append(directory).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var installed in directories)
                await new InstallationPreparationService().PrepareAsync(installed).ConfigureAwait(false);
            result = new(true, "ResoDrive stopped safely. Installation can continue.", DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            var errorId = UiDiagnosticLog.Current.Exception("installer.prepare_failed", exception);
            result = new(false, exception.Message + $" Error ID: {errorId}", DateTimeOffset.UtcNow);
        }
        try
        {
            var paths = new ApplicationPaths();
            Directory.CreateDirectory(paths.Updates);
            await File.WriteAllTextAsync(Path.Combine(paths.Updates, ResultFileName), JsonSerializer.Serialize(result))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UiDiagnosticLog.Current.Exception("installer.prepare_result_failed", exception);
        }
        return result;
    }
}
