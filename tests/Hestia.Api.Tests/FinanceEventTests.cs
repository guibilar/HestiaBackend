using Hestia.Contracts.Finance;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Hestia.Api.Tests;

/// <summary>
/// Finance events through RabbitMQ (D7). A stand-in for the finance service publishes with the same Wolverine routing as
/// finance (the exchange <see cref="FinanceEventTopology.Exchange"/>), and the host's consumers receive the events.
/// These tests need a broker: set HESTIA_TEST_RABBITMQ (CI uses "container").
/// </summary>
public class FinanceEventTests
{
    [Fact]
    public async Task FinanceEvents_FromRabbitMq_ReachTheHostConsumers()
    {
        Assert.SkipUnless(TestRabbitMq.Available, $"Needs RabbitMQ: set {TestRabbitMq.Variable}.");
        var recorder = new EventRecorder();
        await using var host = new HestiaApiFactory(
            new Dictionary<string, string?> { ["Events:RabbitMq"] = TestRabbitMq.Address },
            services => services.AddEventRecorder(recorder, [.. FinanceEventTopology.EventTypes]));
        // Starts the host: it declares the exchange, its queue, and the binding.
        host.CreateClient();
        using var finance = await StartFinanceStandInAsync();

        var imported = new TransactionsImported(Guid.NewGuid(), "Chequing", 42, 40, 2,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), DateTimeOffset.UtcNow);
        var changed = new TransactionChanged(Guid.NewGuid(), TransactionChange.Updated, Guid.NewGuid(),
            new DateOnly(2026, 10, 2), "NO FRILLS #3121", -54.17m, Guid.NewGuid(), "Groceries", DateTimeOffset.UtcNow);
        var budget = new BudgetLimitReached(Guid.NewGuid(), "Groceries", BudgetLimitReached.WarningPercent, 500m, 412.50m,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), DateTimeOffset.UtcNow);
        var balance = new BalanceRecorded(Guid.NewGuid(), "TFSA", new DateOnly(2026, 9, 30), 10234.56m,
            BalanceSource.Statement, DateTimeOffset.UtcNow);
        var price = new SubscriptionPriceChanged("NETFLIX.COM", "Monthly", 16.49m, 18.99m, new DateOnly(2026, 10, 3),
            DateTimeOffset.UtcNow);
        await PublishAsync(finance, imported, changed, budget, balance, price);

        Assert.Equal(imported, await recorder.WaitForAsync<TransactionsImported>());
        Assert.Equal(changed, await recorder.WaitForAsync<TransactionChanged>());
        Assert.Equal(budget, await recorder.WaitForAsync<BudgetLimitReached>());
        Assert.Equal(balance, await recorder.WaitForAsync<BalanceRecorded>());
        Assert.Equal(price, await recorder.WaitForAsync<SubscriptionPriceChanged>());
    }

    [Fact]
    public async Task Host_DoesNotStart_WithAnInvalidRabbitMqAddress()
    {
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["Events:RabbitMq"] = "http://rabbitmq:5672" });

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("Events:RabbitMq", error.ToString());
    }

    /// <summary>Publishes like the finance service: every finance event to the fanout exchange.</summary>
    private static async Task<IHost> StartFinanceStandInAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(wolverine =>
        {
            wolverine.ApplicationAssembly = typeof(FinanceEventTests).Assembly;
            wolverine.Discovery.DisableConventionalDiscovery();
            wolverine.UseRabbitMq(new Uri(TestRabbitMq.Address!))
                .AutoProvision()
                .DeclareExchange(FinanceEventTopology.Exchange, exchange => exchange.ExchangeType = ExchangeType.Fanout);
            foreach (var eventType in FinanceEventTopology.EventTypes)
            {
                wolverine.PublishMessage(eventType).ToRabbitExchange(FinanceEventTopology.Exchange);
            }
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task PublishAsync(IHost finance, params object[] events)
    {
        await using var scope = finance.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var @event in events)
        {
            await bus.PublishAsync(@event);
        }
    }
}
