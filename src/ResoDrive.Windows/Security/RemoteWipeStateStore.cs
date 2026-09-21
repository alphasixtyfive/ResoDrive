using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public enum RemoteWipePhase { Requested, Cleaned, Completed }

public sealed record RemoteWipeState(Guid Id, RemoteWipePhase Phase, RemoteWipeRegistration? Registration,
    bool? ServerAcknowledged = null);

/// <summary>An accepted server command survives crashes until cleanup and acknowledgement finish.</summary>
public sealed class RemoteWipeStateStore(ApplicationPaths paths)
{
    // The state wraps a registration from the 64 KiB catalog and therefore needs
    // more room than either that catalog or the DPAPI store's password default.
    private const int MaximumStateBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public RemoteWipeState? Read()
    {
        try
        {
            // File.Exists also returns false for access errors and directories;
            // neither is evidence that a pending wipe is absent.
            var attributes = File.GetAttributes(paths.RemoteWipeStateFile);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The remote-wipe recovery state is not a regular file. Account access remains blocked.");
            var json = new DpapiSecretStore(paths).LoadProtectedFileAsync(paths.RemoteWipeStateFile, MaximumStateBytes)
                .GetAwaiter().GetResult();
            var state = JsonSerializer.Deserialize<RemoteWipeState>(json, Options);
            Validate(state);
            return state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or InvalidOperationException)
        {
            throw new IOException("The remote-wipe recovery state could not be read. Account access remains blocked.", exception);
        }
    }

    public async Task SaveAsync(RemoteWipeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        var json = JsonSerializer.Serialize(state, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumStateBytes)
            throw new IOException("The remote-wipe recovery state is too large.");
        var staged = paths.RemoteWipeStateFile + ".tmp";
        try
        {
            await new DpapiSecretStore(paths).SaveProtectedFileAsync(
                json, staged, cancellationToken).ConfigureAwait(false);
            using (var flush = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.None))
                flush.Flush(flushToDisk: true);
            File.Move(staged, paths.RemoteWipeStateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static void Validate(RemoteWipeState? state)
    {
        if (state is null || state.Id == Guid.Empty || !Enum.IsDefined(state.Phase) ||
            (state.Phase == RemoteWipePhase.Completed ? state.Registration is not null : state.Registration is null))
            throw new IOException("The remote-wipe recovery state is invalid. Account access remains blocked.");
    }
}
