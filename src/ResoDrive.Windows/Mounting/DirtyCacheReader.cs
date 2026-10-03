using System.Text.Json;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

internal sealed record DirtyCacheStatus(string? MetadataPath, IReadOnlyList<MountUploadFile> Files)
{
    internal static DirtyCacheStatus Empty { get; } = new(null, []);
}

internal static class DirtyCacheReader
{
    private const int MaximumEntries = 100_000;
    private const int MaximumMetadataBytes = 1024 * 1024;

    internal static DirtyCacheStatus ReadFromStatistics(string statistics, string cacheRoot, CancellationToken token)
    {
        using var document = JsonDocument.Parse(statistics);
        if (!document.RootElement.TryGetProperty("opt", out var options))
            throw new JsonException("Missing cache mode.");
        if (VfsTransferStatus.Count(options, "CacheMode") == 0)
            return DirtyCacheStatus.Empty;
        if (!document.RootElement.TryGetProperty("diskCache", out var cache) ||
            !cache.TryGetProperty("pathMeta", out var metadata) || metadata.ValueKind != JsonValueKind.String)
            throw new JsonException("Missing cache metadata location.");
        var path = ValidatePath(metadata.GetString()!, cacheRoot);
        if (!Directory.Exists(path))
        {
            if (VfsTransferStatus.Count(cache, "files") != 0)
                throw new IOException("Cache metadata is unavailable.");
            return new(path, []);
        }
        return Read(path, cacheRoot, token);
    }

    internal static DirtyCacheStatus Read(string metadataPath, string cacheRoot, CancellationToken token)
    {
        var root = ValidatePath(metadataPath, cacheRoot);
        var directories = new Stack<string>();
        directories.Push(root);
        var files = new List<MountUploadFile>();
        var entries = 0;
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoints(directory, cacheRoot);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries)
                    throw new IOException("Cache metadata exceeds the inspection limit.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache metadata contains a linked path.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                if (length > MaximumMetadataBytes)
                    throw new IOException("Cache metadata exceeds the inspection limit.");
                var buffer = new byte[(int)length + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, count, buffer.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count == buffer.Length)
                    throw new IOException("Cache metadata changed during inspection.");
                using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
                if (!document.RootElement.TryGetProperty("Dirty", out var dirty) || dirty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("Incomplete cache metadata.");
                if (!dirty.GetBoolean())
                    continue;
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                relative = VfsTransferStatus.ValidateRelativeName(relative);
                files.Add(new MountUploadFile
                {
                    RelativePath = relative, State = MountUploadState.WaitingForClose,
                    TotalBytes = VfsTransferStatus.Count(document.RootElement, "Size")
                });
            }
        }
        return new(root, files);
    }

    internal static string ValidatePath(string metadataPath, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        var normalized = metadataPath.StartsWith("\\\\?\\", StringComparison.Ordinal) ? metadataPath[4..] : metadataPath;
        if (!Path.IsPathFullyQualified(normalized) || normalized.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Cache metadata must be a managed local path.");
        normalized = Path.GetFullPath(normalized);
        var allowed = Path.GetFullPath(Path.Combine(cacheRoot, "vfsMeta")) + Path.DirectorySeparatorChar;
        if (!normalized.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cache metadata is outside the managed cache.");
        RejectReparsePoints(normalized, cacheRoot);
        return normalized;
    }

    private static void RejectReparsePoints(string path, string cacheRoot)
    {
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        for (var current = path; current.Length >= boundary.Length; current = Path.GetDirectoryName(current) ?? string.Empty)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache metadata contains a linked path.");
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase))
                break;
        }
    }
}
