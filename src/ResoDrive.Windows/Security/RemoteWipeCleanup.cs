namespace ResoDrive.Windows;

public static class RemoteWipeCleanup
{
    public static void DeleteAccountData(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Remote wipe requires a dedicated ResoDrive data directory.");
        ManagedDataPath.ValidateAncestors(root);

        List<Exception> failures = [];
        foreach (var file in Directory.EnumerateFiles(root).Where(IsAccountFile))
            Attempt(() => DeleteFile(file));
        // This fixed app-owned tree remains in scope after jobs or settings are removed.
        // Never derive deletion targets from user-selected sync paths.
        Attempt(() => DeleteDirectory(paths.ManagedSyncRoot));
        Attempt(() => DeleteDirectory(paths.Cache));
        Attempt(() => DeleteDirectory(paths.Logs));
        if (failures.Count != 0)
            throw new IOException("Remote wipe is incomplete. Close applications using managed files and retry.",
                new AggregateException(failures));
        Directory.CreateDirectory(paths.Cache);
        Directory.CreateDirectory(paths.Logs);

        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { failures.Add(exception); }
        }
    }

    private static bool IsAccountFile(string path)
    {
        var name = Path.GetFileName(path).TrimStart('.');
        string[] names = ["settings.json", "rclone.conf", "config-pass.dpapi", "ownership.json",
            "sync-run-state.json", "scheduler-state.json", "welcome.complete", "remote-wipe.dpapi"];
        // Include atomic-write backups and setup staging, but retain the durable wipe state,
        // profiles, components and installers. Never enumerate outside the managed data root.
        return names.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) ||
            name.StartsWith("settings.pre-import-", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (ManagedDataPath.Attributes(path) is not { } attributes) return;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Remote wipe cannot remove a redirected or non-file entry.");
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
            if (ManagedDataPath.Attributes(path) is not { } attributes) return;
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Remote wipe requires an ordinary managed directory.");
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if (ManagedDataPath.Attributes(child) is not { } childAttributes) continue;
                if (childAttributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Remote wipe cannot follow a redirected managed entry.");
                if (childAttributes.HasFlag(FileAttributes.Directory)) DeleteDirectory(child);
                else DeleteFile(child);
            }
            Directory.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Remote wipe could not remove local managed data.", exception);
        }
    }
}
