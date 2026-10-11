using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Persistence;

/// <summary>
/// Sets the database provider for every module DbContext. The host uses PostgreSQL
/// (<see cref="NpgsqlDatabaseOptionsConfigurator"/>); tests can replace it with the in-memory provider.
/// </summary>
public interface IDatabaseOptionsConfigurator
{
    void Configure(DbContextOptionsBuilder options, string schema);
}
