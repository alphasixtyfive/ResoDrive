using System.Diagnostics;
using System.Text.Json;

namespace ResoDrive.Windows;

internal sealed record OwnedMount(Guid MountId, int ProcessId, DateTime StartTimeUtc, string ExecutablePath, string Source, string Target);

internal sealed class MountOwnershipStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public MountOwnershipStore(ApplicationPaths paths)
    {
        _path = paths.OwnershipFile;
        _backupPath = _path + ".bak";
    }

    public async Task<IReadOnlyList<OwnedMount>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(OwnedMount mount, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mounts = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = mounts.Where(item => item.MountId != mount.MountId).Append(mount).ToArray();
            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid mountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mounts = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = mounts.Where(item => item.MountId != mountId).ToArray();
            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Process? TryOpenVerified(OwnedMount owned)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(owned.ProcessId);
            var executable = process.MainModule?.FileName;
            if (Math.Abs((process.StartTime.ToUniversalTime() - owned.StartTimeUtc).TotalSeconds) >= 1 ||
                !IsSameExecutablePath(executable, owned.ExecutablePath))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            return null;
        }
    }

    internal static bool IsDefinitelyGone(OwnedMount owned)
    {
        try
        {
            using var process = Process.GetProcessById(owned.ProcessId);
            return process.HasExited ||
                Math.Abs((process.StartTime.ToUniversalTime() - owned.StartTimeUtc).TotalSeconds) >= 1;
        }
        catch (ArgumentException) { return true; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal async Task<IReadOnlyList<OwnedMount>> LoadForRemoteWipeAsync(CancellationToken cancellationToken)
    {
        var records = await TryReadAsync(_path, cancellationToken).ConfigureAwait(false) ??
            await TryReadAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        if (records is null && (File.Exists(_path) || File.Exists(_backupPath)))
            throw new IOException("Remote wipe cannot verify the recorded mount processes.");
        return records ?? [];
    }

    internal static bool IsSameExecutablePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyList<OwnedMount>> ReadAsync(CancellationToken cancellationToken)
    {
        var primary = await TryReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
            return primary;

        var backup = await TryReadAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        if (backup is not null) return backup;
        if (File.Exists(_path) || File.Exists(_backupPath))
            throw new IOException("The recorded drive processes could not be read. Preserve the cache and repair the ownership records before continuing.");
        return [];
    }

    private static async Task<IReadOnlyList<OwnedMount>?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 1024 * 1024) throw new IOException("Drive process records exceed the inspection limit.");
            var mounts = await JsonSerializer.DeserializeAsync<OwnedMount[]>(stream, Options, cancellationToken).ConfigureAwait(false);
            if (mounts is null || mounts.Any(mount => mount is null || mount.MountId == Guid.Empty ||
                    mount.ProcessId <= 0 || mount.StartTimeUtc == default || string.IsNullOrWhiteSpace(mount.ExecutablePath) ||
                    string.IsNullOrWhiteSpace(mount.Source) || string.IsNullOrWhiteSpace(mount.Target)) ||
                mounts.Select(mount => mount.MountId).Distinct().Count() != mounts.Length)
                throw new JsonException("Invalid drive process records.");
            return mounts;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteAsync(IReadOnlyList<OwnedMount> mounts, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, mounts, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(_path))
                File.Replace(temporary, _path, _backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
