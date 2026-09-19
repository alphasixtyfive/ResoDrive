using System.Security.Cryptography;
using System.Text.Json;

namespace ResoDrive.Windows;

public enum RemoteWipePhase { Requested, Cleaned, Completed }

public sealed record RemoteWipeState(Guid Id, RemoteWipePhase Phase, RemoteWipeRegistration? Registration,
    bool? ServerAcknowledged = null);

/// <summary>An accepted server command survives crashes until cleanup and acknowledgement finish.</summary>
public sealed class RemoteWipeStateStore(ApplicationPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public RemoteWipeState? Read()
    {
        if (!File.Exists(paths.RemoteWipeStateFile))
            return null;
        try
        {
            var json = new DpapiSecretStore(paths).LoadProtectedFileAsync(paths.RemoteWipeStateFile)
                .GetAwaiter().GetResult();
            var state = JsonSerializer.Deserialize<RemoteWipeState>(json, Options);
            if (state is null || state.Id == Guid.Empty || !Enum.IsDefined(state.Phase) ||
                (state.Phase != RemoteWipePhase.Completed && state.Registration is null))
                throw new IOException("The remote-wipe recovery state is invalid. Account access remains blocked.");
            return state;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            throw new IOException("The remote-wipe recovery state could not be read. Account access remains blocked.", exception);
        }
    }

    public async Task SaveAsync(RemoteWipeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var staged = paths.RemoteWipeStateFile + ".tmp";
        try
        {
            await new DpapiSecretStore(paths).SaveProtectedFileAsync(
                JsonSerializer.Serialize(state, Options), staged, cancellationToken).ConfigureAwait(false);
            using (var flush = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.None))
                flush.Flush(flushToDisk: true);
            File.Move(staged, paths.RemoteWipeStateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
