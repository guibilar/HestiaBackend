using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Hestia.Platform.Persistence;

/// <summary>All modules use the one Hestia database, each in its own schema.</summary>
public sealed class NpgsqlDatabaseOptionsConfigurator(IConfiguration configuration) : IDatabaseOptionsConfigurator
{
    public void Configure(DbContextOptionsBuilder options, string schema) =>
        options.UseNpgsql(HestiaDatabase.GetConnectionString(configuration),
            npgsql => HestiaDatabase.ConfigureNpgsql(npgsql, schema));
}
