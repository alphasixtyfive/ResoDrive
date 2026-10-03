namespace ResoDrive.Windows;

/// <summary>Managed files must not redirect traversal outside the app-owned data tree.</summary>
internal static class ManagedDataPath
{
    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Access errors are failures, not proof that data is absent.
    }

    internal static void ValidateAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (Attributes(directory.FullName) is not { } attributes) continue;
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Managed data requires ordinary directories without junctions or symbolic links.");
        }
    }

    internal static void ValidateTree(string path, CancellationToken cancellationToken = default)
    {
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Attributes(directory) is not { } attributes) continue;
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Managed data requires an ordinary directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Attributes(entry) is not { } child) continue;
                if (child.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Managed data cannot contain junctions or symbolic links.");
                if (child.HasFlag(FileAttributes.Directory)) directories.Push(entry);
            }
        }
    }
}
