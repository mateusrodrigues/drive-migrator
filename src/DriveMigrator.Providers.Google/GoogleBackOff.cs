using System.Net;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;

namespace DriveMigrator.Providers.Google;

internal static class GoogleBackOff
{
    /// <summary>Retries throttling (429) and server errors with exponential back-off, for API calls and raw requests alike.</summary>
    public static void Install(IClientService service)
        => service.HttpClient.MessageHandler.AddUnsuccessfulResponseHandler(new BackOffHandler(
            new BackOffHandler.Initializer(new ExponentialBackOff(TimeSpan.FromMilliseconds(500), 6))
            {
                HandleUnsuccessfulResponseFunc = r => r.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError,
            }));
}
