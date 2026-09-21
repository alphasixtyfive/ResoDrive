using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public sealed class RemoteWipeClient : IDisposable
{
    private const int MaximumResponseBytes = 4096;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;
    private string _clientUserAgent;

    public RemoteWipeClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _ownsClient = httpClient is null;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _clientUserAgent = ClientUserAgent.Value;
        // Injected clients must preserve the same redirect and cookie isolation.
        _httpClient = httpClient ?? CreateHttpClient();
    }

    internal static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        // A DAV session cookie from another registration on this server can make
        // an invalid app token appear authenticated. Probe each token independently.
        UseCookies = false
    })
    {
        // The linked deadline covers both requests and the response body.
        Timeout = Timeout.InfiniteTimeSpan
    };

    public void SetClientUserAgent(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Volatile.Write(ref _clientUserAgent, value);
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
        probe.Headers.UserAgent.ParseAdd(Volatile.Read(ref _clientUserAgent));
        AddBasicAuthentication(probe, registration.Username, registration.AppToken);

        try
        {
            using (var response = await _httpClient.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false))
            {
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    return false;
            }

            using var check = CreateTokenRequest(
                serverBase, "index.php/core/wipe/check", registration.AppToken, Volatile.Read(ref _clientUserAgent));
            using var checkResponse = await _httpClient.SendAsync(
                check, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if (checkResponse.StatusCode != HttpStatusCode.OK)
                return false;
            // ResponseHeadersRead does not bound body reads. Apply the same deadline and a small size limit.
            await checkResponse.Content.LoadIntoBufferAsync(MaximumResponseBytes, requestToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await checkResponse.Content.ReadAsByteArrayAsync(requestToken)
                .ConfigureAwait(false));
            return HasExplicitWipeCommand(document.RootElement);
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
        ArgumentNullException.ThrowIfNull(registration);
        if (!TryValidateRegistration(registration, out _, out var serverBase))
            throw new InvalidOperationException("The remote-wipe registration is not a valid HTTPS endpoint.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        using var request = CreateTokenRequest(
            serverBase, "index.php/core/wipe/success", registration.AppToken, Volatile.Read(ref _clientUserAgent));
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        // Nextcloud removes the token on acknowledgement. A retry after a lost response
        // can return 404; it means there is no longer a token to acknowledge, not proof
        // that this specific request reached the server. Local cleanup already finished.
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        // Nextcloud confirms completion with 200. Other 2xx responses (for example
        // a proxy's 202 Accepted) do not prove it finished retiring this token.
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Nextcloud returned HTTP {(int)response.StatusCode} while acknowledging remote wipe.");
        return true;
    }

    private static HttpRequestMessage CreateTokenRequest(
        Uri serverBase, string path, string token, string clientUserAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serverBase, path))
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)])
        };
        request.Headers.UserAgent.ParseAdd(clientUserAgent);
        return request;
    }

    private static bool HasExplicitWipeCommand(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var requested = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("wipe"))
                continue;
            // Reject duplicate/contradictory commands instead of taking the last
            // property's value, as TryGetProperty would do.
            if (requested || property.Value.ValueKind != JsonValueKind.True)
                return false;
            requested = true;
        }
        return requested;
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
