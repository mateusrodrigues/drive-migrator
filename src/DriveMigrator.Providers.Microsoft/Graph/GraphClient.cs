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

    public async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphException(response.StatusCode, "emptyResponse", $"Graph returned an empty body for {url}.");
    }

    /// <summary>Streams every item of a collection, following <c>@odata.nextLink</c> page by page.</summary>
    public async IAsyncEnumerable<T> GetPagedAsync<T>(string url, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetAsync<GraphPage<T>>(next, cancellationToken).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
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

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
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

        return TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
    }
}

internal sealed record GraphPage<T>(List<T> Value)
{
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}
