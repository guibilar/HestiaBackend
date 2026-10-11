using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hestia.Platform.Finance;

namespace Hestia.Api.Gateway;

/// <summary>
/// The shared finance client (D13): one place for the finance address, the auth, the timeout, and the errors. It calls
/// finance directly at <c>FinanceProxy:Address</c>, not through the host's own proxy. Each request has a new gateway
/// token for the signed-in user of the current request (<see cref="GatewayTokens"/>).
/// </summary>
internal sealed class FinanceClient(HttpClient http, IHttpContextAccessor requests, GatewayTokens tokens, ILogger<FinanceClient> logger)
    : IFinanceClient
{
    /// <summary>The finance JSON: camel case, and enums by name.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        return await ReadAsync<T>(request, cancellationToken);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        return await ReadAsync<TResponse>(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request.RequestUri is { IsAbsoluteUri: true })
        {
            throw new ArgumentException("Give the finance path only, for example /api/Accounts. The client adds the address.", nameof(request));
        }
        if (requests.HttpContext?.User is not { Identity.IsAuthenticated: true } user)
        {
            throw new InvalidOperationException(
                "The finance client needs the signed-in user of an HTTP request. Call it while the host handles a request.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create(user));
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException ||
                                          (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FinanceRequestException($"Finance did not answer {request.Method} {request.RequestUri}.", inner: exception);
        }
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Finance answered {Status} to {Method} {Path}", (int)response.StatusCode, request.Method, request.RequestUri);
            throw new FinanceRequestException(
                $"Finance answered {(int)response.StatusCode} to {request.Method} {request.RequestUri}.", response.StatusCode);
        }
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            if (value is null)
            {
                throw new FinanceRequestException($"Finance answered {request.Method} {request.RequestUri} with no content.", response.StatusCode);
            }
            return value;
        }
        catch (JsonException exception)
        {
            throw new FinanceRequestException(
                $"Finance answered {request.Method} {request.RequestUri} with JSON of another shape.", response.StatusCode, exception);
        }
    }
}
