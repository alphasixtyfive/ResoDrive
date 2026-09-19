using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResoDrive.Windows;

public sealed class RemoteWipeClient
{
    private readonly HttpClient _httpClient;

    public RemoteWipeClient(HttpClient? httpClient = null)
    {
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
        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), registration.ProbeEndpoint);
        probe.Headers.Add("Depth", "0");
        probe.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        AddBasicAuthentication(probe, registration.Username, registration.AppToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                return false;
        }

        var checkUri = new Uri(
            new Uri(registration.ServerBaseUrl, UriKind.Absolute),
            "index.php/core/wipe/check");
        using var check = new HttpRequestMessage(HttpMethod.Post, checkUri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", registration.AppToken)])
        };
        check.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        try
        {
            using var checkResponse = await _httpClient.SendAsync(
                check, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (checkResponse.StatusCode != HttpStatusCode.OK)
                return false;
            await using var stream = await checkResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("wipe", out var wipe) &&
                wipe.ValueKind == JsonValueKind.True;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task SignalSuccessAsync(
        RemoteWipeRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var successUri = new Uri(
            new Uri(registration.ServerBaseUrl, UriKind.Absolute),
            "index.php/core/wipe/success");
        using var request = new HttpRequestMessage(HttpMethod.Post, successUri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", registration.AppToken)])
        };
        request.Headers.UserAgent.ParseAdd("ResoDrive-remote-wipe/1.0");
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Nextcloud returned HTTP {(int)response.StatusCode} while acknowledging remote wipe.");
    }

    private static void AddBasicAuthentication(HttpRequestMessage request, string username, string token)
    {
        // The login-flow app password is used as the token parameter and as the
        // Basic password while probing the account's WebDAV endpoint.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));
    }
}
