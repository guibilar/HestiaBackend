using Hestia.Platform.Domain;

namespace Hestia.Modules.Sample.Domain;

/// <summary>One <c>SampleItemCreated</c> event that the Sample consumer handled: the data that the consumer changes.</summary>
public class SampleActivityEntry : BaseEntity
{
    public Guid ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
}
