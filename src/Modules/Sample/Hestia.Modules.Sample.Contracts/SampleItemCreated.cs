namespace Hestia.Modules.Sample.Contracts;

/// <summary>
/// A public event: a sample item was created. Other modules can consume it.
/// T04 adds the in-process publish (after the transaction commits) and the consumers.
/// </summary>
public sealed record SampleItemCreated(Guid ItemId, string Name, DateTimeOffset OccurredAt)
{
    public const int Version = 1;
}
