using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DriveMigrator.Providers.Microsoft.Graph;

/// <summary>A Microsoft Graph error response, with Graph's error code (e.g. "itemNotFound", "accessDenied").</summary>
public sealed class GraphException : Exception
{
    public GraphException()
    {
    }

    public GraphException(string message)
        : base(message)
    {
    }

    public GraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GraphException(HttpStatusCode statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Code { get; }

    internal static async Task<GraphException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string code = response.StatusCode.ToString();
        string message = $"Microsoft Graph request failed with {(int)response.StatusCode} {response.ReasonPhrase}.";
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(GraphClient.JsonOptions, cancellationToken).ConfigureAwait(false);
            if (body?.Error is { } error)
            {
                code = error.Code ?? code;
                message = $"Microsoft Graph: {error.Message ?? message} ({code})";
            }
        }
        catch (JsonException)
        {
            // Not a Graph error body; keep the generic message.
        }

        return new GraphException(response.StatusCode, code, message);
    }

    private sealed record ErrorBody(ErrorDetail? Error);

    private sealed record ErrorDetail(string? Code, string? Message);
}
