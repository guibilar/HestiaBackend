namespace Hestia.Modules.Sample.Contracts;

public sealed record SampleItemDto(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record CreateSampleItemRequest(string? Name);

/// <summary>What the Sample event consumer handled: one entry per SampleItemCreated event, stored in the sample schema.</summary>
public sealed record SampleActivityDto(int ItemsCreated, string? LastItemName);
