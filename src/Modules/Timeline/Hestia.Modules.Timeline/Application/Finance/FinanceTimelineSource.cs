using System.Globalization;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Timeline.Application.Finance;

/// <summary>
/// The Money entries of the Timeline (T16). Finance is a separate service (D6), so its source lives in the Timeline
/// module, and reads finance as the signed-in user (D13):
/// <list type="bullet">
/// <item>each transaction of the days (<c>POST /api/Transaction/Filter</c>, a read-only query), with its category and
/// account; a day of finance has no time;</item>
/// <item>"&lt;Month&gt; in review" on the first day of each month in the range, up to today (T82):
/// <c>GET /api/Reports/review</c> for the month before, with its spending.</item>
/// </list>
/// </summary>
public sealed class FinanceTimelineSource(IFinanceClient finance, ILogger<FinanceTimelineSource> logger) : ITimelineSource
{
    public const string ModuleName = "Money";

    /// <summary>The most transactions on one page (finance's own page limit).</summary>
    public const int MaxTransactions = 1000;

    public string Module => ModuleName;

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var filter = new TransactionFilter(
            range.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            range.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            1, MaxTransactions);
        var transactions = finance.PostAsync<TransactionFilter, TransactionPage>("/api/Transaction/Filter", filter, cancellationToken);
        var reviews = Task.WhenAll(MonthStarts(range).Select(day => ReviewAsync(day, cancellationToken)));
        var page = await transactions;
        var reviewEntries = await reviews;

        var entries = page.Items.Select(Entry).ToList();
        entries.AddRange(reviewEntries.Where(r => r.Entry is not null).Select(r => r.Entry!));
        // More transactions than one page: the oldest are missing.
        var complete = page.TotalCount <= page.Items.Count && reviewEntries.All(r => r.Loaded);
        return new TimelineEntries(entries, complete);
    }

    /// <summary>
    /// The latest transaction day before the range; or the first day of the month after it (the review of that month),
    /// when that day is also before the range.
    /// </summary>
    public async Task<DateOnly?> LatestBeforeAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        // Finance sorts by date, newest first: the first transaction of the page is the latest.
        var filter = new LatestTransactionFilter(range.From.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var page = await finance.PostAsync<LatestTransactionFilter, TransactionPage>("/api/Transaction/Filter", filter, cancellationToken);
        var days = page.Items.Select(t => Entry(t).Day).Where(d => d < range.From).ToList();
        if (days.Count == 0)
        {
            return null;
        }
        var day = days.Max();
        var review = new DateOnly(day.Year, day.Month, 1).AddMonths(1);
        return review < range.From ? review : day;
    }

    private static TimelineEntry Entry(Transaction t)
    {
        // Finance days are calendar days at midnight UTC.
        var day = DateOnly.FromDateTime(t.Date.Kind == DateTimeKind.Local ? t.Date.ToUniversalTime() : t.Date);
        return new TimelineEntry(
            $"money.transaction.{t.Id:D}",
            ModuleName,
            "transaction",
            day,
            null,
            t.Description,
            $"{t.Category?.Name ?? "Other"} · {t.AccountName}",
            t.Amount,
            $"/money/transactions?id={t.Id:D}");
    }

    /// <summary>The first days of a month in the range: each has the review of the month before.</summary>
    public static IEnumerable<DateOnly> MonthStarts(TimelineRange range)
    {
        var first = new DateOnly(range.From.Year, range.From.Month, 1);
        if (first < range.From)
        {
            first = first.AddMonths(1);
        }
        for (var day = first; day <= range.To; day = day.AddMonths(1))
        {
            yield return day;
        }
    }

    /// <summary>
    /// "September in review" on October 1st. No entry for a month without purchases and spending (for example before the
    /// data starts); not loaded when finance did not give the review (the error is logged).
    /// </summary>
    private async Task<(bool Loaded, TimelineEntry? Entry)> ReviewAsync(DateOnly monthStart, CancellationToken cancellationToken)
    {
        var month = monthStart.AddMonths(-1);
        try
        {
            var review = await finance.GetAsync<ReviewReport>(
                $"/api/Reports/review?year={month.Year}&month={month.Month}", cancellationToken);
            if (review.Purchases == 0 && review.Spending == 0)
            {
                return (true, null);
            }
            var name = month.ToString("MMMM", CultureInfo.InvariantCulture);
            var text = review.Purchases == 1 ? "1 purchase" : $"{review.Purchases} purchases";
            if (review.NoSpendDays > 0)
            {
                text += review.NoSpendDays == 1 ? ", 1 day without spending" : $", {review.NoSpendDays} days without spending";
            }
            return (true, new TimelineEntry(
                $"money.month-review.{month:yyyy-MM}",
                ModuleName,
                "month-review",
                monthStart,
                null,
                $"{name} in review",
                text,
                -review.Spending,
                $"/money/review?year={month.Year}&month={month.Month}"));
        }
        catch (FinanceRequestException exception)
        {
            logger.LogWarning(exception, "Timeline: the review of {Month} did not load", month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            return (false, null);
        }
    }
}

// The parts of the finance answers that the source reads (Finance.Contracts in PersonalFinanceInsightsBackend). Each
// module keeps its own view of finance.

/// <summary>The body of <c>POST /api/Transaction/Filter</c>: the days (both included), newest first.</summary>
public sealed record TransactionFilter(DateTime StartDate, DateTime EndDate, int Page, int PageSize);

/// <summary>The latest transaction up to <see cref="EndDate"/> (finance sorts newest first).</summary>
public sealed record LatestTransactionFilter(DateTime EndDate)
{
    public int Page => 1;

    public int PageSize => 1;
}

public sealed record TransactionPage
{
    public List<Transaction> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed record Transaction(Guid Id, DateTime Date, string Description, decimal Amount, CategoryReference? Category, string AccountName);

public sealed record CategoryReference(Guid Id, string Name);

/// <summary><c>GET /api/Reports/review</c> (the parts the source reads).</summary>
public sealed record ReviewReport(decimal Spending, int Purchases, int NoSpendDays);
