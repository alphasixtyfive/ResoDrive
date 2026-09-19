using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public sealed record VfsTransferStatus(long Queued, long Uploading, long Errors, long CacheBytes, bool OutOfSpace)
{
    public bool NeedsAttention => Queued > 0 || Uploading > 0 || Errors > 0 || OutOfSpace;
    public string Description => OutOfSpace
        ? "Cache is out of space. Free local disk space before continuing."
        : Errors > 0
            ? $"{Errors} cache errors · {Queued} queued · {Uploading} uploading"
            : Queued > 0 || Uploading > 0
                ? $"{Queued} queued · {Uploading} uploading"
                : "No queued uploads";

    internal static VfsTransferStatus? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("diskCache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object)
            return null;
        static long Count(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var count) && count >= 0
                ? count : throw new JsonException("Invalid cache statistics.");
        if (!cache.TryGetProperty("outOfSpace", out var outOfSpace) ||
            outOfSpace.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Missing cache space status.");
        return new(Count(cache, "uploadsQueued"), Count(cache, "uploadsInProgress"),
            Count(cache, "erroredFiles"), Count(cache, "bytesUsed"), outOfSpace.GetBoolean());
    }
}

internal static class VfsStatusReader
{
    // Never send control credentials through a proxy or follow a redirect.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    internal static async Task<VfsTransferStatus?> ReadAsync(
        string address, string user, string password, CancellationToken cancellationToken)
    {
        var uri = new Uri($"http://{address}/vfs/stats");
        if (!uri.IsLoopback || uri.Host != "127.0.0.1")
            throw new ArgumentException("The control endpoint must be IPv4 loopback.", nameof(address));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            const int limit = 128 * 1024 + 1;
            var buffer = ArrayPool<byte>.Shared.Rent(limit);
            try
            {
                var count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, limit), limit, throwOnEndOfStream: false, timeout.Token)
                    .ConfigureAwait(false);
                return count == limit ? null : VfsTransferStatus.Parse(Encoding.UTF8.GetString(buffer, 0, count));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
