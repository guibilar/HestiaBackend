namespace Hestia.Modules.Sample.Contracts;

/// <summary>
/// A query interface: how other modules read sample items without the Sample tables.
/// The Sample module registers the implementation.
/// </summary>
public interface ISampleItemQueries
{
    /// <summary>The item, or null when no item has this id.</summary>
    Task<SampleItemDto?> FindAsync(Guid id, CancellationToken cancellationToken = default);
}
