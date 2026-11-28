using System.Diagnostics;
using System.IO;
using ResoDrive.Windows;

namespace ResoDrive.App;

/// <summary>Temporary old-path launcher. Relays activation from the real application window, including migration startup.</summary>
internal static class LegacyInstallationBridge
{
    internal static int Run(string[] arguments)
    {
        if (!File.Exists(InstallationDirectories.Executable)) return 1;
        using var activation = new SingleInstanceActivation(App.CreateInstanceScope(InstallationDirectories.Legacy));
        if (!activation.IsFirstInstance) return activation.RequestShow(TimeSpan.FromMinutes(2)) ? 0 : 1;
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activation.Listen(request =>
        {
            _ = Task.Run(() =>
            {
                if (SingleInstanceActivation.RequestShow(App.CreateInstanceScope(InstallationDirectories.Current), TimeSpan.FromMinutes(2)))
                {
                    request.Acknowledge();
                    _ = acknowledged.TrySetResult(true);
                }
                else request.Dispose();
            });
        });
        var start = new ProcessStartInfo(InstallationDirectories.Executable)
        {
            UseShellExecute = false, WorkingDirectory = InstallationDirectories.Current,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var configured = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configured) && Directory.Exists(ApplicationPaths.LegacyDefaultRoot))
            start.Environment["RDRIVE_DATA_DIR"] = ApplicationPaths.LegacyDefaultRoot;
        start.Environment[DirectoryMigrationStartup.BridgeEnvironmentVariable] = "1";
        using var application = Process.Start(start) ?? throw new IOException("The migrated application could not be opened.");
        // With a live legacy updater, keep its old activation endpoint until it
        // receives readiness. A standalone old shortcut simply forwards the launch.
        using var helpers = DirectoryMigrationStartup.FindLegacyHelpers();
        if (helpers.Processes.Count == 0) return 0;
        return acknowledged.Task.Wait(TimeSpan.FromMinutes(3)) ? 0 : 1;
    }
}
