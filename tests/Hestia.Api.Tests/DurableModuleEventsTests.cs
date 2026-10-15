using Hestia.Api.Events;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Api.Tests;

/// <summary>
/// T78: no module event is lost. A module event in memory (a Wolverine local queue) is lost when the process stops before
/// its consumers ran, and after the retries of a consumer that fails (the host has no message storage). A durable event
/// (T38) stays in the module's outbox until all its consumers ran, and a row that failed too many times stays for
/// <c>retry-outbox</c> and the "stuck outbox" alert (T37, T80). So every module that has events uses durable events.
/// </summary>
[Collection(StartedHostCollection.Name)]
public class DurableModuleEventsTests(StartedHost host)
{
    /// <summary>The Sample module shows both modes (the setting <c>Sample:DurableEvents</c>). No other module is here.</summary>
    private static readonly HashSet<string> InMemoryAllowed = ["Sample"];

    [Fact]
    public void EveryModuleWithEvents_UsesDurableEvents()
    {
        var modules = ModuleLoader.Discover();
        var databases = host.Services.GetServices<ModuleDatabase>().ToList();
        var withEvents = modules
            .Where(module => HestiaEvents.ModuleEventTypes([module]).Count > 0)
            .ToList();

        // A guard against an empty list: these modules have events.
        Assert.Contains(withEvents, module => module.Name == "Documents");
        Assert.Contains(withEvents, module => module.Name == "Tasks");

        var problems = withEvents
            .Where(module => !InMemoryAllowed.Contains(module.Name))
            .Select(module => (module, database: databases.SingleOrDefault(d => d.ContextType.Assembly == module.GetType().Assembly)))
            .Where(pair => pair.database is not { DurableEvents: true })
            .Select(pair => $"{pair.module.Name} has events, but its DbContext does not use durable events. Register it with " +
                "database => database.UseDurableEvents() and call modelBuilder.AddEventOutbox() (docs/new-module.md, step 6 \"Events\").")
            .ToList();
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }
}
