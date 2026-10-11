namespace Hestia.Contracts.Finance;

// The events that the finance service publishes (D7). Finance writes each event to its outbox in the same database
// transaction as the change, and sends it to RabbitMQ after the commit. A consumer can get an event more than once:
// make each consumer idempotent.
//
// Money: amounts are in the account's currency (CAD), with two decimals. Dates without a time are the day in the
// finance reporting time zone.

/// <summary>A statement was imported into an account. It is published also when the import added nothing new.</summary>
/// <param name="AccountId">The account that the statement belongs to.</param>
/// <param name="AccountName">The name of the account when the import happened.</param>
/// <param name="Read">The rows in the statement.</param>
/// <param name="Added">The rows that were new and are now stored.</param>
/// <param name="Skipped">The rows that were stored already.</param>
/// <param name="FirstDate">The date of the oldest row in the statement, or null when the statement had no rows.</param>
/// <param name="LastDate">The date of the newest row in the statement, or null when the statement had no rows.</param>
/// <param name="OccurredAt">When the import finished.</param>
public sealed record TransactionsImported(
    Guid AccountId,
    string AccountName,
    int Read,
    int Added,
    int Skipped,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;
}

/// <summary>What happened to a transaction.</summary>
public enum TransactionChange
{
    /// <summary>A person added the transaction by hand.</summary>
    Added,

    /// <summary>The description, the category, the flow, the note, the tags, or the budget link changed.</summary>
    Updated,

    /// <summary>A person deleted the transaction. Finance keeps it hidden, so that a new import does not add it again.</summary>
    Deleted,

    /// <summary>A person restored a deleted transaction.</summary>
    Restored
}

/// <summary>
/// A transaction changed. Imported transactions do not each send this event: <see cref="TransactionsImported"/> tells
/// that an account has new transactions.
/// </summary>
/// <param name="TransactionId">The transaction.</param>
/// <param name="Change">What happened.</param>
/// <param name="AccountId">The account of the transaction.</param>
/// <param name="Date">The day of the transaction.</param>
/// <param name="Description">The description after the change.</param>
/// <param name="Amount">Money in is positive, money out is negative.</param>
/// <param name="CategoryId">The category after the change.</param>
/// <param name="CategoryName">The name of the category after the change.</param>
/// <param name="OccurredAt">When the change happened.</param>
public sealed record TransactionChanged(
    Guid TransactionId,
    TransactionChange Change,
    Guid AccountId,
    DateOnly Date,
    string Description,
    decimal Amount,
    Guid CategoryId,
    string CategoryName,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;
}

/// <summary>
/// The spending of a budget went up to a threshold in the current period: 80 % (a warning) or 100 % (the limit).
/// Finance sends it when a change makes the spending cross the threshold, not on each change after that.
/// If the spending goes down below the threshold and up again, finance sends it again.
/// </summary>
/// <param name="BudgetId">The budget.</param>
/// <param name="BudgetName">The name of the budget.</param>
/// <param name="Percent">The threshold that the spending reached: <see cref="WarningPercent"/> or <see cref="LimitPercent"/>.</param>
/// <param name="Limit">The amount of the budget.</param>
/// <param name="Spent">The spending in the period after the change.</param>
/// <param name="PeriodStart">The first day of the current period, or null for a budget without a period (a total budget).</param>
/// <param name="PeriodEnd">The last day of the current period, or null for a budget without a period.</param>
/// <param name="OccurredAt">When the change happened.</param>
public sealed record BudgetLimitReached(
    Guid BudgetId,
    string BudgetName,
    int Percent,
    decimal Limit,
    decimal Spent,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public const int WarningPercent = 80;
    public const int LimitPercent = 100;

    public int Version { get; init; } = 1;
}

/// <summary>Where a recorded balance came from.</summary>
public enum BalanceSource
{
    /// <summary>A person entered the balance.</summary>
    Entered,

    /// <summary>An imported statement gave the balance (for example, the portfolio value of an investment statement).</summary>
    Statement
}

/// <summary>A balance of an account was recorded for a day. A new balance for the same day replaces the old one.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="AccountName">The name of the account.</param>
/// <param name="Date">The day of the balance.</param>
/// <param name="Amount">What the account holds at the end of the day. For a credit card: what the card owes.</param>
/// <param name="Source">Where the balance came from.</param>
/// <param name="OccurredAt">When the balance was recorded.</param>
public sealed record BalanceRecorded(
    Guid AccountId,
    string AccountName,
    DateOnly Date,
    decimal Amount,
    BalanceSource Source,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;
}

/// <summary>
/// A recurring payment (a subscription) has a new fixed price. Finance sends it when a new charge with the new price
/// arrives (an import, or a transaction added by hand).
/// </summary>
/// <param name="Merchant">The merchant, as the finance category rules name it (for example "NETFLIX.COM").</param>
/// <param name="Cadence">How often the payment comes: Weekly, Biweekly, Monthly, Quarterly, or Yearly.</param>
/// <param name="OldPrice">The price before.</param>
/// <param name="NewPrice">The new price.</param>
/// <param name="Since">The day of the first charge at the new price.</param>
/// <param name="OccurredAt">When finance found the change.</param>
public sealed record SubscriptionPriceChanged(
    string Merchant,
    string Cadence,
    decimal OldPrice,
    decimal NewPrice,
    DateOnly Since,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;
}

/// <summary>The RabbitMQ names for the finance events. Finance and the host use the same names.</summary>
public static class FinanceEventTopology
{
    /// <summary>The fanout exchange that finance publishes all its events to.</summary>
    public const string Exchange = "hestia.finance-events";

    /// <summary>The queue of Hestia.Api, bound to <see cref="Exchange"/>.</summary>
    public const string HostQueue = "hestia.api.finance-events";

    /// <summary>All finance event types.</summary>
    public static IReadOnlyList<Type> EventTypes { get; } =
    [
        typeof(TransactionsImported),
        typeof(TransactionChanged),
        typeof(BudgetLimitReached),
        typeof(BalanceRecorded),
        typeof(SubscriptionPriceChanged)
    ];
}
