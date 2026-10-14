namespace Hestia.Modules.Sample.Application;

// T47 probe: a consumer with a dependency that Wolverine cannot build. Do not merge.

public interface IProbeService;

public sealed class ProbeService : IProbeService;

public sealed record ProbeEvent(Guid EventId);

public sealed class ProbeConsumer(IProbeService service)
{
    public void Consume(ProbeEvent message) => GC.KeepAlive(service);
}
