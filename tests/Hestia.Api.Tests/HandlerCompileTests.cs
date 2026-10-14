using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Hestia.Api.Tests;

/// <summary>
/// Every Wolverine handler (consumer) of the host compiles, with all its dependencies (T47). Wolverine compiles a handler
/// at its first message, so a dependency that it cannot build (for example a service registered with a lambda, without
/// <c>AlwaysUseServiceLocationFor</c>) fails only then, and the message goes to the error queue. This test compiles all
/// handlers in CI instead.
/// </summary>
public class HandlerCompileTests
{
    [Fact]
    public async Task EveryHandler_Compiles()
    {
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();

        var (count, problems) = HandlerCompileCheck.Run(factory.Services);

        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine + Environment.NewLine, problems));
        }
        // A guard against an empty handler list: the module consumers and the debug consumer are there.
        Assert.True(count >= 10, $"Only {count} handlers were found.");
    }

    [Fact]
    public async Task AHandlerWithADependencyThatWolverineCannotBuild_FailsTheCheck_WithItsNames()
    {
        await using var factory = new HestiaApiFactory(configureServices: services =>
        {
            // A lambda registration: Wolverine cannot build it in its generated code.
            services.AddScoped<ILambdaOnlyService>(_ => new LambdaOnlyService());
            services.AddSingleton<IWolverineExtension>(new AddBadConsumer());
        });
        factory.CreateClient();

        var (_, problems) = HandlerCompileCheck.Run(factory.Services);

        var problem = Assert.Single(problems);
        Assert.Contains(typeof(BadTestConsumer).FullName!, problem);
        Assert.Contains(typeof(BadTestEvent).FullName!, problem);
        Assert.Contains(nameof(ILambdaOnlyService), problem);
        Assert.Contains("AlwaysUseServiceLocationFor", problem);
    }

    public interface ILambdaOnlyService;

    public sealed class LambdaOnlyService : ILambdaOnlyService;

    public sealed record BadTestEvent(Guid EventId);

    /// <summary>A consumer that takes a service that Wolverine cannot build.</summary>
    public sealed class BadTestConsumer(ILambdaOnlyService service)
    {
        public void Consume(BadTestEvent message) => GC.KeepAlive(service);
    }

    public sealed class AddBadConsumer : IWolverineExtension
    {
        public void Configure(WolverineOptions options) => options.Discovery.IncludeType(typeof(BadTestConsumer));
    }
}

/// <summary>Compiles each Wolverine handler chain of a started host (T47).</summary>
public static class HandlerCompileCheck
{
    /// <summary>
    /// Builds the handler of each message type, as Wolverine does at the first message. Returns the number of chains, and
    /// one message for each chain that does not compile.
    /// </summary>
    public static (int Count, List<string> Problems) Run(IServiceProvider services)
    {
        var graph = services.GetRequiredService<HandlerGraph>();
        var problems = new List<string>();
        var chains = graph.Chains;
        foreach (var chain in chains)
        {
            // Read the names first: the code generation can change the chain.
            var handlers = string.Join(", ", chain.Handlers.Select(call => call.HandlerType.FullName).Distinct());
            try
            {
                graph.HandlerFor(chain.MessageType);
                foreach (var sticky in chain.ByEndpoint)
                {
                    foreach (var endpoint in sticky.Endpoints)
                    {
                        graph.HandlerFor(chain.MessageType, endpoint);
                    }
                }
            }
            catch (Exception exception)
            {
                problems.Add(
                    $"The handler of {chain.MessageType.FullName} does not compile. Handler classes: {handlers}." +
                    Environment.NewLine + Describe(exception) + Environment.NewLine +
                    "A dependency registered with a lambda (AddDbContext, a typed HttpClient, a factory) needs " +
                    "wolverine.CodeGeneration.AlwaysUseServiceLocationFor<T>() in HestiaEventsSetup " +
                    "(src/Hestia.Api/Events/HestiaEvents.cs), or a registration by type (AddScoped<TService, TImplementation>()).");
            }
        }
        return (chains.Length, problems);
    }

    private static string Describe(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            lines.Add($"{current.GetType().Name}: {current.Message.Trim()}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
