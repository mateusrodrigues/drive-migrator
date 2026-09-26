using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveMigrator.Providers.Microsoft.Graph;

/// <summary>
/// Minimal Microsoft Graph client: bearer auth, JSON, paging through <c>@odata.nextLink</c>, and retries that
/// honour Graph throttling (429/503/504 with Retry-After).
/// </summary>
internal sealed class GraphClient(
    HttpClient http,
    Func<CancellationToken, Task<string>> getAccessToken,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    internal const int MaxRetries = 5;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public Task<T> GetAsync<T>(string url, CancellationToken cancellationToken, string? prefer = null)
        => SendJsonAsync<T>(HttpMethod.Get, url, body: null, cancellationToken, prefer);

    /// <summary>GET that returns null instead of throwing when Graph answers 404.</summary>
    public async Task<T?> GetOrDefaultAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await GetAsync<T>(url, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Sends an optional JSON body and reads a JSON response.</summary>
    /// <summary>
    /// Sends an optional JSON body and reads a JSON response. <paramref name="prefer"/> is sent as a Prefer header,
    /// e.g. <c>outlook.timezone="UTC"</c>.
    /// </summary>
    public async Task<T> SendJsonAsync<T>(HttpMethod method, string url, object? body, CancellationToken cancellationToken, string? prefer = null)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: JsonOptions) };
                if (prefer is not null)
                {
                    request.Headers.TryAddWithoutValidation("Prefer", prefer);
                }

                return request;
            },
            cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphException(response.StatusCode, "emptyResponse", $"Graph returned an empty body for {url}.");
    }

    /// <summary>Sends an authenticated request with throttling retries; the caller owns the response.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getAccessToken(cancellationToken).ConfigureAwait(false));

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (IsTransient(response.StatusCode) && attempt < MaxRetries)
                {
                    await _delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await GraphException.FromResponseAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends to a pre-authorized URL (e.g. an upload session) without a bearer token and without retries; the caller
    /// owns the response and decides how to retry.
    /// </summary>
    public Task<HttpResponseMessage> SendUnauthenticatedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => _delay(delay, cancellationToken);

    internal static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return BackOff(attempt);
    }

    internal static TimeSpan BackOff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    /// <summary>Streams every item of a collection, following <c>@odata.nextLink</c> page by page.</summary>
    public async IAsyncEnumerable<T> GetPagedAsync<T>(string url, [EnumeratorCancellation] CancellationToken cancellationToken, string? prefer = null)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetAsync<GraphPage<T>>(next, cancellationToken, prefer).ConfigureAwait(false);
            foreach (var item in page.Value)
            {
                yield return item;
            }

            if (page.NextLink == next)
            {
                throw new GraphException(HttpStatusCode.OK, "pagingLoop", $"Microsoft Graph returned the same next page link twice for {url}.");
            }

            next = page.NextLink;
        }
    }

}

internal sealed record GraphPage<T>(List<T> Value)
{
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}
