using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public sealed class RemoteWipeClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;

    public RemoteWipeClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _ownsClient = httpClient is null;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<bool> IsWipeRequestedAsync(
        RemoteWipeRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!TryValidateRegistration(registration, out var probeEndpoint, out var serverBase))
            return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var requestToken = deadline.Token;

        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), probeEndpoint);
        probe.Headers.Add("Depth", "0");
        probe.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        AddBasicAuthentication(probe, registration.Username, registration.AppToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                return false;
        }

        var checkUri = new Uri(serverBase, "index.php/core/wipe/check");
        using var check = new HttpRequestMessage(HttpMethod.Post, checkUri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", registration.AppToken)])
        };
        check.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        try
        {
            using var checkResponse = await _httpClient.SendAsync(
                check, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if (checkResponse.StatusCode != HttpStatusCode.OK)
                return false;
            // ResponseHeadersRead does not bound body reads. Apply the same deadline and a small size limit.
            await checkResponse.Content.LoadIntoBufferAsync(4096, requestToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await checkResponse.Content.ReadAsByteArrayAsync(requestToken)
                .ConfigureAwait(false));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("wipe", out var wipe) &&
                wipe.ValueKind == JsonValueKind.True;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<bool> SignalSuccessAsync(
        RemoteWipeRegistration registration,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRegistration(registration, out _, out var serverBase))
            throw new InvalidOperationException("The remote-wipe registration is not a valid HTTPS endpoint.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var successUri = new Uri(serverBase, "index.php/core/wipe/success");
        using var request = new HttpRequestMessage(HttpMethod.Post, successUri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", registration.AppToken)])
        };
        request.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        // Nextcloud removes the token on acknowledgement. A retry after a lost response
        // can return 404; it means there is no longer a token to acknowledge, not proof
        // that this specific request reached the server. Local cleanup already finished.
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Nextcloud returned HTTP {(int)response.StatusCode} while acknowledging remote wipe.");
        return true;
    }

    private static void AddBasicAuthentication(HttpRequestMessage request, string username, string token)
    {
        // The login-flow app password is used as the token parameter and as the
        // Basic password while probing the account's WebDAV endpoint.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));
    }

    private static bool TryValidateRegistration(
        RemoteWipeRegistration registration,
        out Uri probeEndpoint,
        out Uri serverBase)
    {
        probeEndpoint = null!;
        serverBase = null!;
        if (!Uri.TryCreate(registration.ProbeEndpoint, UriKind.Absolute, out var parsedProbe) ||
            !Uri.TryCreate(registration.ServerBaseUrl, UriKind.Absolute, out var parsedServer))
            return false;
        probeEndpoint = parsedProbe;
        serverBase = parsedServer;
        if (string.IsNullOrWhiteSpace(registration.Username) || string.IsNullOrWhiteSpace(registration.AppToken) ||
            !string.Equals(probeEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(serverBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(probeEndpoint.Host, serverBase.Host, StringComparison.OrdinalIgnoreCase) ||
            probeEndpoint.Port != serverBase.Port ||
            !string.IsNullOrEmpty(serverBase.Query) || !string.IsNullOrEmpty(serverBase.Fragment) ||
            !string.IsNullOrEmpty(probeEndpoint.Query) || !string.IsNullOrEmpty(probeEndpoint.Fragment) ||
            !string.IsNullOrEmpty(probeEndpoint.UserInfo) || !string.IsNullOrEmpty(serverBase.UserInfo))
            return false;
        serverBase = new Uri(serverBase.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
