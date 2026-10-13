using Hestia.Contracts.Finance;
using Hestia.Modules.Sample.Contracts;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Api.Tests;

/// <summary>
/// The host's part of durable module events (T38): the outbox format and the sender settings. The tests of the full
/// path (save, restart, send) are in <c>Hestia.Modules.Sample.Tests/DurableEventTests.cs</c>.
/// </summary>
public class ModuleOutboxTests
{
    [Fact]
    public void Serializer_KeepsTheEvent_WithItsEventId()
    {
        using var factory = new HestiaApiFactory();
        var serializer = factory.Services.GetRequiredService<IModuleEventSerializer>();
        var sent = new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);

        var typeName = serializer.TypeName(sent);
        var received = serializer.Deserialize(typeName, serializer.Serialize(sent));

        Assert.Equal(typeof(SampleItemCreated).FullName, typeName);
        Assert.Equal(sent, received);
    }

    [Fact]
    public void Serializer_RefusesAnEventThatIsNotAModuleEvent()
    {
        using var factory = new HestiaApiFactory();
        var serializer = factory.Services.GetRequiredService<IModuleEventSerializer>();

        // A finance event comes from finance, not from a module. A plain object is no event.
        Assert.Throws<InvalidOperationException>(() =>
            serializer.TypeName(new BudgetLimitReached(Guid.NewGuid(), "Food", 120, 100m, 120m, null, null, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => serializer.TypeName(new object()));
    }

    [Fact]
    public void Serializer_RefusesAStoredTypeThatNoModuleHas()
    {
        using var factory = new HestiaApiFactory();
        var serializer = factory.Services.GetRequiredService<IModuleEventSerializer>();

        var error = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize("Hestia.Modules.Gone.Contracts.ThingAdded", "{}"));

        Assert.Contains("Hestia.Modules.Gone.Contracts.ThingAdded", error.Message);
    }

    [Theory]
    [InlineData("Events:Outbox:MaxAttempts", "0")]
    [InlineData("Events:Outbox:PollInterval", "00:00:00")]
    [InlineData("Events:Outbox:RetryDelay", "00:00:00")]
    public void InvalidOutboxSettings_StopTheHost(string key, string value)
    {
        using var factory = new HestiaApiFactory(new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Outbox", error.ToString());
    }
}
