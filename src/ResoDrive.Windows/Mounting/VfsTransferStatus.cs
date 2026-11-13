using System.Text.Json;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

public sealed record VfsTransferStatus(long Queued, long Uploading, long Errors, long CacheBytes, bool OutOfSpace)
{
    internal const int MaximumVisibleFiles = 100;
    public long Dirty { get; init; }
    public long? BytesRemaining { get; init; }
    public double? SpeedBytesPerSecond { get; init; }
    public double? EtaSeconds { get; init; }
    public IReadOnlyList<MountUploadFile> Files { get; init; } = [];
    public bool DetailsTruncated { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    internal string? MetadataPath { get; init; }
    internal bool UsesDiskCache { get; init; } = true;
    public bool NeedsAttention => Queued > 0 || Uploading > 0 || Dirty > 0 || Errors > 0 || OutOfSpace;
    public string Description => OutOfSpace
        ? "Cache is out of space. Free local disk space before continuing."
        : Errors > 0
            ? $"{Errors} cache errors · {Queued} queued · {Uploading} uploading · {Dirty} waiting for close"
            : Queued > 0 || Uploading > 0 || Dirty > 0
                ? $"{Queued} queued · {Uploading} uploading · {Dirty} waiting for close"
                : "No queued uploads";

    internal static VfsTransferStatus? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("diskCache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object)
            return null;
        return new(Count(cache, "uploadsQueued"), Count(cache, "uploadsInProgress"),
            Count(cache, "erroredFiles"), Count(cache, "bytesUsed"), Boolean(cache, "outOfSpace"));
    }

    internal static VfsTransferStatus ParseComplete(string statistics, string queueJson, string coreJson,
        DirtyCacheStatus dirty)
    {
        using var vfs = JsonDocument.Parse(statistics);
        using var queue = JsonDocument.Parse(queueJson);
        using var core = JsonDocument.Parse(coreJson);
        if (vfs.RootElement.ValueKind != JsonValueKind.Object ||
            !vfs.RootElement.TryGetProperty("fs", out var fs) || fs.ValueKind != JsonValueKind.String ||
            !vfs.RootElement.TryGetProperty("opt", out var options) || Count(options, "CacheMode") > 3)
            throw new JsonException("Incomplete VFS statistics.");
        var cached = Count(options, "CacheMode") > 0;
        var baseline = cached ? Parse(statistics) ?? throw new JsonException("Missing disk cache statistics.")
            : new VfsTransferStatus(0, 0, 0, 0, false);
        if (queue.RootElement.ValueKind != JsonValueKind.Object || core.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Incomplete transfer statistics.");
        // With cache mode off, rclone returns {} rather than { "queue": [] }.
        // Only that documented response may omit the queue; core/stats still
        // accounts for direct uploads that never enter the disk cache.
        var hasQueue = queue.RootElement.TryGetProperty("queue", out var queueItems);
        if (hasQueue ? queueItems.ValueKind != JsonValueKind.Array : cached || queue.RootElement.EnumerateObject().Any())
            throw new JsonException("Incomplete upload queue.");
        _ = Count(core.RootElement, "errors");
        _ = Count(core.RootElement, "bytes");
        _ = Boolean(core.RootElement, "fatalError");
        _ = Boolean(core.RootElement, "retryError");
        var files = new Dictionary<string, MountUploadFile>(StringComparer.Ordinal);
        var dirtyNames = dirty.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        IEnumerable<JsonElement> queuedFiles = hasQueue ? queueItems.EnumerateArray() : [];
        foreach (var item in queuedFiles)
        {
            var name = RelativeName(item, "name");
            var tries = Count(item, "tries");
            files[name] = new MountUploadFile
            {
                RelativePath = name, TotalBytes = Count(item, "size"),
                State = Boolean(item, "uploading") ? MountUploadState.Uploading
                    : tries > 0 ? MountUploadState.Retrying : MountUploadState.Queued
            };
        }
        if (core.RootElement.TryGetProperty("transferring", out var active))
        {
            if (active.ValueKind != JsonValueKind.Array)
                throw new JsonException("Invalid active transfer statistics.");
            foreach (var item in active.EnumerateArray())
            {
                var name = RelativeName(item, "name");
                // rclone exposes hydration as srcFs=the mounted remote with no
                // destination filesystem. Unknown direction stays conservative.
                if (!files.ContainsKey(name) && !dirtyNames.Contains(name) &&
                    item.TryGetProperty("srcFs", out var sourceFs) && sourceFs.ValueKind == JsonValueKind.String &&
                    sourceFs.GetString()?.TrimEnd('/') == fs.GetString()?.TrimEnd('/') &&
                    (!item.TryGetProperty("dstFs", out var destinationFs) || destinationFs.ValueKind == JsonValueKind.Null))
                    continue;
                var size = SignedCount(item, "size");
                var transferred = Count(item, "bytes");
                files[name] = new MountUploadFile
                {
                    RelativePath = name, State = MountUploadState.Uploading,
                    TotalBytes = size < 0 ? null : size,
                    BytesTransferred = size < 0 ? transferred : Math.Min(transferred, size),
                    SpeedBytesPerSecond = OptionalNumber(item, "speedAvg") ?? OptionalNumber(item, "speed"),
                    EtaSeconds = OptionalNumber(item, "eta")
                };
            }
        }
        foreach (var item in dirty.Files)
            files.TryAdd(item.RelativePath, item);
        var all = files.Values.ToArray();
        var queued = all.LongCount(x => x.State is MountUploadState.Queued or MountUploadState.Retrying);
        var uploading = all.LongCount(x => x.State == MountUploadState.Uploading);
        var open = all.LongCount(x => x.State == MountUploadState.WaitingForClose);
        // Separate RC calls can straddle a transfer transition. Preserve positive
        // aggregate counts even if the detail disappeared; 100% is not acceptance.
        queued = Math.Max(queued, baseline.Queued);
        uploading = Math.Max(uploading, baseline.Uploading);
        var totalKnown = all.All(x => x.TotalBytes is not null) && queued + uploading + open == all.Length;
        var remaining = totalKnown ? SaturatingSum(all.Select(x => Math.Max(0, x.TotalBytes!.Value - x.BytesTransferred))) : (long?)null;
        var speed = all.Where(x => x.State == MountUploadState.Uploading).Sum(x => x.SpeedBytesPerSecond ?? 0);
        var errors = Math.Max(baseline.Errors, all.LongCount(x => x.State == MountUploadState.Retrying));
        return baseline with
        {
            Queued = queued, Uploading = uploading, Errors = errors, Dirty = open,
            BytesRemaining = remaining, SpeedBytesPerSecond = speed > 0 ? speed : null,
            EtaSeconds = remaining is > 0 && speed > 0 && open == 0 ? remaining / speed : null,
            Files = all.OrderBy(x => x.State == MountUploadState.Uploading ? 0 : 1)
                .ThenBy(x => x.RelativePath, StringComparer.Ordinal).Take(MaximumVisibleFiles).ToArray(),
            DetailsTruncated = all.Length > MaximumVisibleFiles,
            MetadataPath = dirty.MetadataPath, ObservedAt = DateTimeOffset.UtcNow, UsesDiskCache = cached
        };
    }

    internal static long Count(JsonElement element, string name)
    {
        var count = SignedCount(element, name);
        return count >= 0 ? count : throw new JsonException("Invalid cache statistics.");
    }
    private static long SignedCount(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count)
            ? count : throw new JsonException("Invalid transfer statistics.");
    private static bool Boolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new JsonException("Invalid cache status.");
    private static double? OptionalNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0
            ? number : throw new JsonException("Invalid transfer progress.");
    }
    internal static string RelativeName(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException("Missing transfer name.");
        return ValidateRelativeName(value.GetString()!);
    }
    internal static string ValidateRelativeName(string relativeName)
    {
        var name = relativeName.Replace('\\', '/');
        if (name.Length is 0 or > 4096 || name.StartsWith('/') || name.Any(char.IsControl) ||
            name.Split('/').Any(x => x is "" or "." or ".." || x.Contains(':')))
            throw new JsonException("Invalid relative transfer name.");
        return name;
    }
    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
            total = value > long.MaxValue - total ? long.MaxValue : total + value;
        return total;
    }
}
