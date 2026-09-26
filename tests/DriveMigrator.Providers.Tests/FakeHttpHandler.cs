using System.Net;
using System.Text;

namespace DriveMigrator.Providers.Tests;

/// <summary>Answers HTTP requests from a script, recording what was asked. No network access.</summary>
internal sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken),
            request.Content?.Headers.ContentRange?.ToString(),
            request.Content?.Headers.ContentType?.MediaType);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return respond(recorded);
    }
}

/// <summary>A copy of a request taken before HttpClient disposes it.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri RequestUri, string? Authorization, byte[]? Body, string? ContentRange, string? ContentType)
{
    public string Url => RequestUri.AbsoluteUri;

    public string BodyText => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);
}
