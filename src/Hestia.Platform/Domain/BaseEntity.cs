namespace Hestia.Platform.Domain;

/// <summary>The base class for entities that have a Guid key.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; }
}
