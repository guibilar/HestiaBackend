using System.Reflection;
using Hestia.Contracts;
using Hestia.Contracts.Finance;
using Hestia.Platform.Events;
using Hestia.Platform.Modules;
using JasperFx;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Hestia.Api.Events;

/// <summary>The <c>Events</c> settings of the host.</summary>
public sealed class EventsOptions
{
    public const string SectionName = "Events";

    /// <summary>
    /// The RabbitMQ broker that finance publishes its events to, for example <c>amqp://hestia:secret@rabbitmq:5672</c>.
    /// Empty: the host does not listen for finance events. The in-process module events work without it.
    /// </summary>
    public string? RabbitMq { get; set; }

    /// <summary>
    /// A debug consumer logs every event (finance and module events) at Information level. The host reads it for each
    /// event, so a change in appsettings.json takes effect without a restart.
    /// </summary>
    public bool LogAll { get; set; }
}

/// <summary>
/// Events in the host (D7), with Wolverine:
/// <list type="bullet">
/// <item>Module events: in process. A module raises them on its DbContext; the platform publishes them after the commit
/// (<see cref="ModuleEventsInterceptor"/>) to Wolverine local queues.</item>
/// <item>Finance events: from RabbitMQ, when <see cref="EventsOptions.RabbitMq"/> is set. Finance writes them to its
/// outbox and sends them to the exchange <see cref="FinanceEventTopology.Exchange"/>.</item>
/// </list>
/// A consumer is a Wolverine handler (a public class whose name ends in "Consumer", with a <c>Consume</c> method) in a
/// module project. It does not know which path an event used.
/// </summary>
public static class HestiaEvents
{
    public static WebApplicationBuilder AddHestiaEvents(this WebApplicationBuilder builder, IReadOnlyList<IModule> modules)
    {
        builder.Services.AddOptions<EventsOptions>().BindConfiguration(EventsOptions.SectionName);
        // Before the modules register their DbContexts: the platform keeps this one (TryAdd).
        builder.Services.AddScoped<IEventPublisher, WolverineEventPublisher>();
        // The setup reads the Events settings when the host starts (an extension from the container), so it sees the
        // final configuration, test settings included.
        builder.Services.AddSingleton<IWolverineExtension>(services =>
            new HestiaEventsSetup(services.GetRequiredService<IConfiguration>(), modules));
        builder.UseWolverine(_ => { });
        return builder;
    }

    /// <summary>The finance events and the events in each module's <c>.Contracts</c> assembly.</summary>
    public static IReadOnlyList<Type> EventTypes(IEnumerable<IModule> modules)
    {
        var types = new List<Type>(FinanceEventTopology.EventTypes);
        foreach (var module in modules)
        {
            var name = module.GetType().Assembly.GetName().Name + ".Contracts";
            var contracts = Assembly.Load(new AssemblyName(name));
            types.AddRange(contracts.GetExportedTypes().Where(type =>
                type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
                typeof(IIntegrationEvent).IsAssignableFrom(type)));
        }
        return types.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
    }
}

/// <summary>The Wolverine setup of the host: handler discovery, the debug consumer, and the RabbitMQ listener.</summary>
internal sealed class HestiaEventsSetup(IConfiguration configuration, IReadOnlyList<IModule> modules) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        var options = configuration.GetSection(EventsOptions.SectionName).Get<EventsOptions>() ?? new EventsOptions();

        // Explicit, so a test host finds the same handlers (Wolverine otherwise guesses from the call stack).
        wolverine.ApplicationAssembly = typeof(HestiaEvents).Assembly;
        foreach (var assembly in modules.Select(module => module.GetType().Assembly).Distinct())
        {
            wolverine.Discovery.IncludeAssembly(assembly);
        }

        // Always registered, and quiet unless LogAll is on: so the set of handlers does not depend on a setting.
        foreach (var eventType in HestiaEvents.EventTypes(modules))
        {
            wolverine.Discovery.IncludeType(typeof(EventLog<>).MakeGenericType(eventType));
        }

        if (!string.IsNullOrWhiteSpace(options.RabbitMq))
        {
            // Declares the exchange, the queue, and the binding when they do not exist (idempotent).
            wolverine.UseRabbitMq(ParseRabbitMq(options.RabbitMq))
                .AutoProvision()
                .DeclareExchange(FinanceEventTopology.Exchange, exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Fanout;
                    exchange.BindQueue(FinanceEventTopology.HostQueue);
                });
            // Inline: the broker removes a message only after its consumers finished. A consumer that fails
            // gets the Wolverine retries; after them, the message goes to the dead-letter queue.
            wolverine.ListenToRabbitQueue(FinanceEventTopology.HostQueue).ProcessInline();

            // The host is the gateway: a broker that is down when the host starts must not stop it. The host then
            // starts without the finance events and logs the error. Restart the host when the broker is back.
            wolverine.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;
        }
    }

    private static Uri ParseRabbitMq(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                $"{EventsOptions.SectionName}:RabbitMq must be an amqp:// or amqps:// address, for example amqp://user:password@rabbitmq:5672.");
        }
        return uri;
    }
}

/// <summary>Publishes module events to their in-process consumers (Wolverine local queues).</summary>
internal sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public ValueTask PublishAsync(object @event, CancellationToken cancellationToken = default) => bus.PublishAsync(@event);
}

/// <summary>
/// The debug consumer (<c>Events:LogAll</c>): one handler per event type, which logs the event with all its values when
/// the setting is on. It is a consumer like any other, so it shows what consumers receive.
/// </summary>
public sealed class EventLog<TEvent>(IOptionsMonitor<EventsOptions> options, ILogger<EventLog<TEvent>> logger) where TEvent : class
{
    public void Consume(TEvent message)
    {
        if (options.CurrentValue.LogAll)
        {
            logger.LogInformation("Event received: {Event}", message);
        }
    }
}
