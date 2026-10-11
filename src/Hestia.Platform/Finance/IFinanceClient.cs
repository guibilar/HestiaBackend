using System.Net;

namespace Hestia.Platform.Finance;

/// <summary>
/// The one way for a module to call the finance backend (D13). The host implements it: it calls finance directly at
/// <c>FinanceProxy:Address</c> (not through its own proxy), with the gateway token of the signed-in user of the
/// current request, and with the finance timeouts. Use it inside a request; outside a request there is no user.
/// <para>Background code (an event consumer, a timer, a host command) has no user. It asks for the system identity
/// explicitly with <see cref="AsSystem"/> (T42).</para>
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

    /// <summary>
    /// A client for background work (T42): it calls finance as the Hestia system identity, not as a user. Use it in an
    /// event consumer, a timer, or a host command. The access is read-only unless the caller asks for
    /// <see cref="FinanceAccess.Full"/>, so a write from background code is a deliberate choice in code.
    /// <para>The client refuses to work inside an HTTP request, so an endpoint cannot reach finance as the system
    /// identity. An endpoint uses this client itself, with the signed-in user.</para>
    /// </summary>
    /// <param name="caller">
    /// A short name of the background code, for example <c>Alerts.BudgetConsumer</c>: letters, digits, '.', '-', and '_',
    /// 1 to 64 characters. The host logs it with each call (Debug level), and finance sees it in the token.
    /// </param>
    /// <param name="access">Read-only (the default) or full access.</param>
    /// <exception cref="ArgumentException"><paramref name="caller"/> is not a valid name.</exception>
    /// <exception cref="InvalidOperationException">The call happens inside an HTTP request.</exception>
    IFinanceClient AsSystem(string caller, FinanceAccess access = FinanceAccess.ReadOnly);
}

/// <summary>What the system identity can do in finance (<see cref="IFinanceClient.AsSystem"/>).</summary>
public enum FinanceAccess
{
    /// <summary>GET, and the finance read-only POST queries. The default.</summary>
    ReadOnly,

    /// <summary>All calls, writes too. Ask for it only when the background code must change finance data.</summary>
    Full
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
