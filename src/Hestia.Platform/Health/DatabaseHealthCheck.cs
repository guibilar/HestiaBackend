using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hestia.Platform.Health;

/// <summary>Healthy when every module's DbContext can connect to the database.</summary>
public class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var failed = new List<string>();
        foreach (var database in scope.ServiceProvider.GetServices<ModuleDatabase>())
        {
            var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(database.ContextType);
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                failed.Add(database.Schema);
            }
        }
        return failed.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Cannot connect to the database for: {string.Join(", ", failed)}.");
    }
}
