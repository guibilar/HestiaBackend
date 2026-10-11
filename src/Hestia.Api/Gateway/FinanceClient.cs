using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hestia.Platform.Auth;
using Hestia.Platform.Finance;

namespace Hestia.Api.Gateway;

/// <summary>
/// The shared finance client (D13): one place for the finance address, the auth, the timeout, and the errors. It calls
/// finance directly at <c>FinanceProxy:Address</c>, not through the host's own proxy. Each request has a new gateway
/// token for the signed-in user of the current request (<see cref="GatewayTokens"/>).
/// <para>A client from <see cref="AsSystem"/> (T42) uses the token of the system identity instead, and works only outside
/// an HTTP request.</para>
/// </summary>
internal sealed partial class FinanceClient : IFinanceClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _requests;
    private readonly GatewayTokens _tokens;
    private readonly ILogger<FinanceClient> _logger;

    /// <summary>Null for the client of the signed-in user.</summary>
    private readonly SystemCaller? _system;

    private sealed record SystemCaller(string Name, FinanceAccess Access)
    {
        public string Scope => Access == FinanceAccess.Full ? HestiaUser.FullScope : HestiaUser.ReadOnlyScope;
    }

    public FinanceClient(HttpClient http, IHttpContextAccessor requests, GatewayTokens tokens, ILogger<FinanceClient> logger)
        : this(http, requests, tokens, logger, system: null)
    {
    }

    private FinanceClient(HttpClient http, IHttpContextAccessor requests, GatewayTokens tokens, ILogger<FinanceClient> logger,
        SystemCaller? system)
    {
        _http = http;
        _requests = requests;
        _tokens = tokens;
        _logger = logger;
        _system = system;
    }

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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(request));
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException ||
                                          (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FinanceRequestException($"Finance did not answer {request.Method} {request.RequestUri}.", inner: exception);
        }
    }

    public IFinanceClient AsSystem(string caller, FinanceAccess access = FinanceAccess.ReadOnly)
    {
        if (string.IsNullOrEmpty(caller) || !CallerName().IsMatch(caller))
        {
            throw new ArgumentException(
                "The caller name must have 1 to 64 letters, digits, '.', '-', or '_', for example Alerts.BudgetConsumer.", nameof(caller));
        }
        if (!Enum.IsDefined(access))
        {
            throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown finance access.");
        }
        RefuseInsideARequest();
        return new FinanceClient(_http, _requests, _tokens, _logger, new SystemCaller(caller, access));
    }

    /// <summary>The token of the signed-in user, or of the system identity for a system client.</summary>
    private string CreateToken(HttpRequestMessage request)
    {
        if (_system is not null)
        {
            // Checked again here: a system client made in background code can be kept and used later in a request.
            RefuseInsideARequest();
            _logger.LogDebug("{Caller} calls finance as the system identity ({Scope}): {Method} {Path}",
                _system.Name, _system.Scope, request.Method, request.RequestUri);
            return _tokens.CreateForSystem(_system.Name, _system.Scope);
        }
        if (_requests.HttpContext?.User is not { Identity.IsAuthenticated: true } user)
        {
            throw new InvalidOperationException(
                "The finance client needs the signed-in user of an HTTP request. Call it while the host handles a request. " +
                "Background code (a consumer, a timer, a command) uses AsSystem(...).");
        }
        return _tokens.Create(user);
    }

    /// <summary>The system identity is for background work only. An HTTP request uses the client of its own user.</summary>
    private void RefuseInsideARequest()
    {
        if (_requests.HttpContext is not null)
        {
            throw new InvalidOperationException(
                "The system finance client works only outside an HTTP request (in a consumer, a timer, or a command). " +
                "An endpoint calls finance with the signed-in user: use IFinanceClient without AsSystem.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex CallerName();

    private async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Finance answered {Status} to {Method} {Path}", (int)response.StatusCode, request.Method, request.RequestUri);
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
