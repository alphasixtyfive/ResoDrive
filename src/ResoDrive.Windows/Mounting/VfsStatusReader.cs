using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

internal static class VfsStatusReader
{
    // Never send control credentials through a proxy or follow a redirect.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    internal static async Task<VfsTransferStatus?> ReadAsync(string address, string user, string password,
        string managedCacheRoot, CancellationToken cancellationToken)
    {
        var uri = ControlEndpoint(address);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var statisticsTask = ReadJsonAsync(uri, "vfs/stats", user, password, timeout.Token);
            var queueTask = ReadJsonAsync(uri, "vfs/queue", user, password, timeout.Token);
            var coreTask = ReadJsonAsync(uri, "core/stats", user, password, timeout.Token);
            await Task.WhenAll(statisticsTask, queueTask, coreTask).ConfigureAwait(false);
            if (statisticsTask.Result is not { } statistics || queueTask.Result is not { } queue || coreTask.Result is not { } core)
                return null;
            var dirty = await Task.Run(() => DirtyCacheReader.ReadFromStatistics(statistics, managedCacheRoot, timeout.Token), timeout.Token)
                .ConfigureAwait(false);
            return VfsTransferStatus.ParseComplete(statistics, queue, core, dirty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static async Task<bool> RequestQuitAsync(string address, string user, string password,
        CancellationToken cancellationToken)
    {
        var endpoint = ControlEndpoint(address);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await ReadJsonAsync(endpoint, "core/quit", user, password, timeout.Token).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return false; }
    }

    private static Uri ControlEndpoint(string address)
    {
        var uri = new Uri($"http://{address}/");
        if (!uri.IsLoopback || uri.Host != "127.0.0.1" || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The control endpoint must be IPv4 loopback.", nameof(address));
        return uri;
    }

    private static async Task<string?> ReadJsonAsync(Uri endpoint, string method, string user, string password, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, method));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadResponseAsync(stream, token).ConfigureAwait(false);
    }

    internal static async Task<string?> ReadResponseAsync(Stream stream, CancellationToken token,
        ArrayPool<byte>? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        buffers ??= ArrayPool<byte>.Shared;
        // Most control responses are small. Rent larger buffers only when their
        // contents require it; clearing three maximum-size rentals on each health
        // poll otherwise touches megabytes even for a few hundred bytes of JSON.
        const int limit = 1024 * 1024 + 1;
        var buffer = buffers.Rent(8 * 1024);
        try
        {
            var count = 0;
            while (count < limit)
            {
                token.ThrowIfCancellationRequested();
                var capacity = Math.Min(buffer.Length, limit);
                if (count == capacity)
                {
                    var larger = buffers.Rent(Math.Min(capacity * 2, limit));
                    buffer.AsSpan(0, count).CopyTo(larger);
                    buffers.Return(buffer, clearArray: true);
                    buffer = larger;
                    capacity = Math.Min(buffer.Length, limit);
                }
                var read = await stream.ReadAsync(buffer.AsMemory(count, capacity - count), token).ConfigureAwait(false);
                if (read == 0) return Encoding.UTF8.GetString(buffer, 0, count);
                count += read;
            }
            // One byte beyond the maximum proves this response is too large.
            return null;
        }
        finally { buffers.Return(buffer, clearArray: true); }
    }
}
