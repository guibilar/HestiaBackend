using System.Net;

namespace Hestia.Platform.Finance;

/// <summary>
/// The one way for a module to call the finance backend (D13). The host implements it: it calls finance directly at
/// <c>FinanceProxy:Address</c> (not through its own proxy), with the gateway token of the signed-in user of the
/// current request, and with the finance timeouts. Use it inside a request; outside a request there is no user.
/// </summary>
public interface IFinanceClient
{
    /// <summary>GET <paramref name="pathAndQuery"/> (for example <c>/api/Accounts</c>) and reads the JSON answer.</summary>
    /// <exception cref="FinanceRequestException">Finance does not answer, or answers with an error status.</exception>
    Task<T> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken = default);

    /// <summary>POST <paramref name="body"/> as JSON to <paramref name="path"/> and reads the JSON answer.</summary>
    /// <exception cref="FinanceRequestException">Finance does not answer, or answers with an error status.</exception>
    Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends any request, with the gateway token. The caller reads the answer and its status. Use the request's path
    /// only (for example <c>/api/Reports/summary</c>): the client adds the finance address.
    /// </summary>
    /// <exception cref="FinanceRequestException">Finance does not answer.</exception>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A finance call failed. <see cref="StatusCode"/> is null when finance did not answer (down, or a timeout).
/// The host answers 502 to its client (see <c>ApiExceptionHandler</c>).
/// </summary>
public sealed class FinanceRequestException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
