using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hestia.Platform.Health;

/// <summary>
/// The answer of <c>/health/ready</c> (T64): the overall status, and the name and status of each check, as JSON.
/// <code>
/// {"status":"Unhealthy","checks":[{"name":"database","status":"Healthy"},{"name":"finance","status":"Unhealthy"}]}
/// </code>
/// The endpoint is open (no sign-in), so the answer has no descriptions, exception messages, paths, or data of the
/// checks. The log of the host has them. The status code does not change: 200 for Healthy and Degraded, 503 for
/// Unhealthy.
/// </summary>
public static class HealthResponse
{
    public const string ContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The body: the checks in name order, so two answers are easy to compare.</summary>
    public static HealthAnswer From(HealthReport report) => new(
        report.Status,
        report.Entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new HealthCheckAnswer(entry.Key, entry.Value.Status))
            .ToList());

    /// <summary>A <c>ResponseWriter</c> for <c>HealthCheckOptions</c>.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = ContentType;
        return context.Response.WriteAsync(JsonSerializer.Serialize(From(report), Json), context.RequestAborted);
    }
}

/// <summary>The JSON body of <c>/health/ready</c>.</summary>
public sealed record HealthAnswer(HealthStatus Status, IReadOnlyList<HealthCheckAnswer> Checks);

/// <summary>One check in <see cref="HealthAnswer"/>.</summary>
public sealed record HealthCheckAnswer(string Name, HealthStatus Status);
