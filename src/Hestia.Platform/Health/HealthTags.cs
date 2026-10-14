namespace Hestia.Platform.Health;

/// <summary>The tags of the health checks.</summary>
public static class HealthTags
{
    /// <summary>
    /// A check of <c>/health/ready</c>: the host can serve requests. A module adds its own readiness check with
    /// <c>services.AddHealthChecks().AddCheck&lt;T&gt;("name", tags: [HealthTags.Ready])</c>. <c>/health/live</c> runs no checks.
    /// </summary>
    public const string Ready = "ready";
}
