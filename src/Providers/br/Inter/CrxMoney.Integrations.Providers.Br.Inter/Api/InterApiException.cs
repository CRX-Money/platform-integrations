using System.Net;
using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

/// <summary>An Inter call failed. <see cref="Exception.Message"/> is safe to log and to show: it carries no secrets.</summary>
internal sealed class InterApiException(IntegrationErrorKind kind, HttpStatusCode? statusCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public IntegrationErrorKind Kind { get; } = kind;

    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>Maps a non-success status to the contract's error kinds.</summary>
    public static IntegrationErrorKind KindFor(HttpStatusCode statusCode, bool isTokenCall) => statusCode switch
    {
        HttpStatusCode.Unauthorized => IntegrationErrorKind.Authentication,
        HttpStatusCode.Forbidden when isTokenCall => IntegrationErrorKind.Authentication,
        HttpStatusCode.Forbidden => IntegrationErrorKind.Permission,
        HttpStatusCode.BadRequest when isTokenCall => IntegrationErrorKind.Authentication,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => IntegrationErrorKind.Validation,
        HttpStatusCode.TooManyRequests => IntegrationErrorKind.RateLimited,
        HttpStatusCode.RequestTimeout => IntegrationErrorKind.Network,
        _ => IntegrationErrorKind.Upstream,
    };
}
