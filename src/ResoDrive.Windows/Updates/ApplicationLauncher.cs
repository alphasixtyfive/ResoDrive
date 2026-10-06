using System.Diagnostics;

namespace ResoDrive.Windows;

/// <summary>Keeps the managed executable identity stable while supervising interactive launches.</summary>
public static class ApplicationLauncher
{
    public const string FileName = "resodrive-launcher.exe";
    public const string SupervisedEnvironmentVariable = "RDRIVE_CRASH_SUPERVISED";

    public static string Resolve(string applicationPath)
    {
        var fullPath = Path.GetFullPath(applicationPath);
        if (!Path.GetFileName(fullPath).Equals("resodrive.exe", StringComparison.OrdinalIgnoreCase))
            return fullPath;
        var launcher = Path.Combine(Path.GetDirectoryName(fullPath)!, FileName);
        try
        {
            return File.Exists(launcher) &&
                (File.GetAttributes(launcher) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? launcher : fullPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return fullPath;
        }
    }

    public static ProcessStartInfo CreateStartInfo(string applicationPath)
    {
        var info = new ProcessStartInfo(Resolve(applicationPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(applicationPath)),
        };
        // A supervisor belongs to one process, never to all of its descendants.
        info.Environment.Remove(SupervisedEnvironmentVariable);
        return info;
    }
}
