using System.Text.Json;
using System.Text.Json.Serialization;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

/// <summary>Persists only terminal sync outcomes; live progress remains transient.</summary>
internal sealed class SyncRunStateStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncRunStateStore(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        _path = paths.SyncRunStateFile;
    }

    public IReadOnlyList<SyncSnapshot> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) return [];
            var entries = JsonSerializer.Deserialize<List<PersistedSyncRun>>(stream, SerializerOptions) ?? [];
            return entries
                .Where(IsValid)
                .Select(ToSnapshot)
                .GroupBy(item => item.JobId)
                .Select(group => group.MaxBy(item => item.CompletedAt)!)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Historical status is optional and must never prevent the host from starting.
            return [];
        }
    }

    public async Task SaveAsync(SyncSnapshot snapshot)
    {
        if (!IsTerminal(snapshot) || snapshot.CompletedAt is null)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var current = Load().ToDictionary(item => item.JobId);
            current[snapshot.JobId] = snapshot;
            var entries = current.Values
                .OrderBy(item => item.JobId.Value)
                .Select(FromSnapshot)
                .ToArray();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entries,
                    SerializerOptions,
                    CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A transfer result remains authoritative even when optional history cannot be saved.
        }
        finally
        {
            TryDelete(temporaryPath);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsValid(PersistedSyncRun? value) =>
        value is not null && value.MountId != Guid.Empty &&
        value.JobId != Guid.Empty &&
        value.CompletedAt is not null &&
        value.StatusText is { Length: > 0 and <= 300 } &&
        value.Lifecycle is SyncLifecycle.Succeeded or SyncLifecycle.Failed or SyncLifecycle.Cancelled;

    private static bool IsTerminal(SyncSnapshot value) =>
        value.Lifecycle is SyncLifecycle.Succeeded or SyncLifecycle.Failed or SyncLifecycle.Cancelled;

    private static PersistedSyncRun FromSnapshot(SyncSnapshot value) => new(
        value.MountId.Value,
        value.JobId.Value,
        value.Lifecycle,
        value.CompletedAt,
        value.StatusText ?? value.Lifecycle.ToString(),
        value.BytesTransferred,
        value.TotalBytes,
        value.ProgressPercent,
        value.ChecksCompleted,
        value.TotalChecks,
        value.TransfersCompleted,
        value.TotalTransfers,
        value.Errors,
        value.SpeedBytesPerSecond,
        value.EtaSeconds,
        value.ElapsedSeconds);

    private static SyncSnapshot ToSnapshot(PersistedSyncRun value) => new()
    {
        MountId = new MountId(value.MountId),
        JobId = new SyncJobId(value.JobId),
        Lifecycle = value.Lifecycle,
        CompletedAt = value.CompletedAt,
        StatusText = value.StatusText,
        BytesTransferred = value.BytesTransferred,
        TotalBytes = value.TotalBytes,
        ProgressPercent = value.ProgressPercent,
        ChecksCompleted = value.ChecksCompleted,
        TotalChecks = value.TotalChecks,
        TransfersCompleted = value.TransfersCompleted,
        TotalTransfers = value.TotalTransfers,
        Errors = value.Errors,
        SpeedBytesPerSecond = value.SpeedBytesPerSecond,
        EtaSeconds = value.EtaSeconds,
        ElapsedSeconds = value.ElapsedSeconds
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedSyncRun(
        Guid MountId,
        Guid JobId,
        SyncLifecycle Lifecycle,
        DateTimeOffset? CompletedAt,
        string? StatusText,
        long? BytesTransferred,
        long? TotalBytes,
        double? ProgressPercent,
        long? ChecksCompleted,
        long? TotalChecks,
        long? TransfersCompleted,
        long? TotalTransfers,
        long? Errors,
        double? SpeedBytesPerSecond,
        double? EtaSeconds,
        double? ElapsedSeconds);
}
