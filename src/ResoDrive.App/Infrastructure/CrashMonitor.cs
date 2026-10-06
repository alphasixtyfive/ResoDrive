using System.Diagnostics;
using System.Globalization;
using System.IO;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class CrashMonitor
{
    internal const int FailureAlreadyDisplayedExitCode = unchecked((int)0xE0524447);
    internal const int ActivationTimedOutExitCode = unchecked((int)0xE0524448);
    internal static bool ConsumeSupervisedMarker()
    {
        var supervised = Environment.GetEnvironmentVariable(ApplicationLauncher.SupervisedEnvironmentVariable) == "1";
        Environment.SetEnvironmentVariable(ApplicationLauncher.SupervisedEnvironmentVariable, null);
        return supervised;
    }

    internal static ProcessStartInfo CreateObserverStartInfo(string observerPath, int processId,
        long creationFileTime, string role)
    {
        if (processId <= 0 || creationFileTime <= 0 || role is not ("ui" or "host"))
            throw new ArgumentException("The crash observer identity is invalid.");
        var startInfo = new ProcessStartInfo(observerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--observe");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(creationFileTime.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--role");
        startInfo.ArgumentList.Add(role);
        startInfo.Environment.Remove(ApplicationLauncher.SupervisedEnvironmentVariable);
        return startInfo;
    }

    internal static void ObserveCurrentProcess(string role, bool alreadySupervised)
    {
        if (alreadySupervised) return;
        try
        {
            var observer = Path.Combine(AppContext.BaseDirectory, ApplicationLauncher.FileName);
            if (!File.Exists(observer))
            {
                UiDiagnosticLog.Current.Information("diagnostics.observer_unavailable");
                return;
            }
            using var current = Process.GetCurrentProcess();
            using var process = Process.Start(CreateObserverStartInfo(observer, current.Id,
                current.StartTime.ToUniversalTime().ToFileTimeUtc(), role));
            UiDiagnosticLog.Current.Information(process is null
                ? "diagnostics.observer_unavailable" : "diagnostics.observer_started");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            UiDiagnosticLog.Current.Exception("diagnostics.observer_failed", exception);
        }
    }
}
