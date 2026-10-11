namespace Hestia.Modules.Sample.Contracts;

public sealed record SampleItemDto(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record CreateSampleItemRequest(string? Name);
