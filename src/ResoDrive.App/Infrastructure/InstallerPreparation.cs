using System.IO;
using System.Text.Json;
using System.Windows;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed record InstallerPreparationResult(bool Succeeded, string Message, DateTimeOffset RecordedAtUtc);

internal static class InstallerPreparation
{
    internal const string ResultFileName = "installer-preparation.json";

    internal static int Run(string installationDirectory, bool showProgress)
    {
        if (!showProgress)
            return PrepareAsync(installationDirectory, null).GetAwaiter().GetResult().Succeeded ? 0 : 1;

        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/resodrive;component/Themes/Controls.xaml", UriKind.Relative)
        });
        var window = new InstallerPreparationWindow();
        window.Loaded += async (_, _) =>
        {
            var result = await PrepareAsync(installationDirectory,
                new Progress<string>(message => window.SetStage(message)));
            if (result.Succeeded)
            {
                window.Finish();
                application.Shutdown(0);
            }
            else
                window.ShowFailure(result.Message);
        };
        window.Closed += (_, _) => application.Shutdown(1);
        return application.Run(window);
    }

    private static async Task<InstallerPreparationResult> PrepareAsync(string directory, IProgress<string>? progress)
    {
        InstallerPreparationResult result;
        try
        {
            UiDiagnosticLog.Current.Information("installer.prepare_started");
            await new InstallationPreparationService().PrepareAsync(directory, progress).ConfigureAwait(false);
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
