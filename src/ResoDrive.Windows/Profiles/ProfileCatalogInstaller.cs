using System.Text;
using ResoDrive.Core.Results;
using ResoDrive.Core.Setup;

namespace ResoDrive.Windows;

/// <summary>Installs deployment templates in the active, migration-ready data root.</summary>
public sealed class ProfileCatalogInstaller(ApplicationPaths paths)
{
    private const int MaximumBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // True means an existing valid user catalog was preserved.
    public async Task<OperationResult<bool>> InstallAsync(string source, CancellationToken cancellationToken = default)
    {
        string? staged = null;
        try
        {
            if (paths.Root.StartsWith(@"\\", StringComparison.Ordinal))
                return Result.Failure<bool>("profiles.data_root_invalid", "Profile installation requires a local data folder.");
            var accountData = new AccountDataGuard(paths);
            if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
                return Result.Failure<bool>("profiles.source_invalid", "Choose a local profile catalog file.");
            ManagedDataPath.ValidateAncestors(Path.GetDirectoryName(source)!);
            var json = await ReadCatalogAsync(source, cancellationToken).ConfigureAwait(false);
            var parsed = SetupProfileCatalogJson.Parse(json);
            if (!parsed.Succeeded || parsed.Value is not { Count: > 0 })
                return Result.Failure<bool>("profiles.invalid", "The supplied profile catalog is invalid or empty.");

            ManagedDataPath.ValidateAncestors(paths.Root);
            await using var lease = await accountData.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ManagedDataPath.ValidateAncestors(paths.Root);
            var existing = ManagedDataPath.Attributes(paths.ProfilesFile);
            if (existing is not null)
            {
                ValidateFile(existing.Value);
                // Oversized/invalid catalogs are backed up; access failures stop the write.
                if (new FileInfo(paths.ProfilesFile).Length <= MaximumBytes)
                {
                    try
                    {
                        if (SetupProfileCatalogJson.Parse(await ReadCatalogAsync(paths.ProfilesFile,
                            cancellationToken).ConfigureAwait(false)).Succeeded) return Result.Success(true);
                    }
                    catch (DecoderFallbackException) { }
                }
            }

            staged = paths.ProfilesFile + $".{Guid.NewGuid():N}.tmp";
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(StrictUtf8.GetBytes(json), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (existing is null) File.Move(staged, paths.ProfilesFile);
            else File.Replace(staged, paths.ProfilesFile,
                paths.ProfilesFile + $".invalid-{Guid.NewGuid():N}", ignoreMetadataErrors: true);
            return Result.Success(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Result.Failure<bool>("profiles.install_failed",
                "Profile templates could not be saved. Existing settings were preserved. Check access and pending data cleanup.");
        }
        finally
        {
            if (staged is not null)
                try { File.Delete(staged); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> ReadCatalogAsync(string path, CancellationToken token)
    {
        ValidateFile(File.GetAttributes(path));
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (file.Length > MaximumBytes) throw new IOException("Profile catalog exceeds the size limit.");
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await file.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumBytes) throw new IOException("Profile catalog exceeds the size limit.");
        return StrictUtf8.GetString(bytes, 0, length).TrimStart('\uFEFF');
    }

    private static void ValidateFile(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Profile catalogs require ordinary files.");
    }
}
