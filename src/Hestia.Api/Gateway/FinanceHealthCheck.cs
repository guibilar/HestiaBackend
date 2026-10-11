using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Gateway;

/// <summary>Healthy when the finance backend answers its readiness endpoint with a success status.</summary>
public sealed class FinanceHealthCheck(IHttpClientFactory clients, IOptions<FinanceProxyOptions> options) : IHealthCheck
{
    public const string HttpClientName = "finance-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var address = new Uri(settings.Address!, settings.HealthPath);
        try
        {
            using var response = await clients.CreateClient(HttpClientName).GetAsync(address, cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Finance answered {(int)response.StatusCode} on {settings.HealthPath}.");
        }
        catch (Exception exception) when ((exception is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Finance does not answer.", exception);
        }
    }
}
