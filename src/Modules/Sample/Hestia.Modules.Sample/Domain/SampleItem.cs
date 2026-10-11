using Hestia.Platform.Domain;

namespace Hestia.Modules.Sample.Domain;

public class SampleItem : BaseEntity
{
    public const int MaxNameLength = 200;

    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
