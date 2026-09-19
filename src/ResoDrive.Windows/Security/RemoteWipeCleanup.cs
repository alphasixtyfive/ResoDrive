namespace ResoDrive.Windows;

public static class RemoteWipeCleanup
{
    public static void DeleteAccountData(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        DeleteFile(paths.SettingsFile);
        DeleteFile(paths.ConfigFile);
        DeleteFile(paths.ConfigSecretFile);
        DeleteFile(paths.OwnershipFile);
        DeleteFile(paths.SyncRunStateFile);
        DeleteFile(paths.WelcomeCompletedFile);
        DeleteDirectory(paths.Cache);
        DeleteDirectory(paths.Logs);
        Directory.CreateDirectory(paths.Cache);
        Directory.CreateDirectory(paths.Logs);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Remote wipe could not remove '{Path.GetFileName(path)}'.", exception);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Remote wipe could not remove local cached data.", exception);
        }
    }
}
