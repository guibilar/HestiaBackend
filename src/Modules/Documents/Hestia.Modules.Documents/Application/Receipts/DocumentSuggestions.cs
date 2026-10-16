using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application.Receipts;

/// <summary>
/// The suggestions of a document (T100): what <see cref="ReceiptReader"/> finds in its OCR text, and the finance
/// transactions with the same amount near the same day (through finance, as the signed-in user).
/// </summary>
public sealed class DocumentSuggestions(
    DocumentsDbContext db,
    IFinanceClient finance,
    IOptions<DocumentsOptions> options,
    TimeProvider time,
    ILogger<DocumentSuggestions> logger)
{
    /// <summary>A transaction can be up to this many days from the receipt (a card payment posts a day or two later).</summary>
    public const int MatchDays = 3;

    public const int MaxMatches = 5;

    public async Task<DocumentSuggestionsDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().Include(d => d.Links)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Document", id);
        if (document.OcrStatus != OcrStatus.Done)
        {
            return new DocumentSuggestionsDto(id, document.OcrStatus, null, null, null, null, null, null, [], true);
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Value.GetTimeZone()).DateTime);
        var facts = ReceiptReader.Read(document.OcrText, today);
        var start = facts.Date ?? document.IssuedOn;
        var warrantyUntil = facts.WarrantyUntil ?? (facts.WarrantyMonths is { } months && start is { } day ? day.AddMonths(months) : null);
        DocumentKind? kind = document.Kind != DocumentKind.Other ? null
            : facts.Total is not null && (facts.Date is not null || facts.Merchant is not null) ? DocumentKind.Receipt
            : warrantyUntil is not null ? DocumentKind.Warranty
            : null;

        var (matches, complete) = facts is { Total: { } total, Date: { } date }
            ? await MatchesAsync(total, date, document.Links.Where(l => l.Kind == DocumentLinkKinds.MoneyTransaction)
                .Select(l => l.TargetId).ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken)
            : ([], true);
        return new DocumentSuggestionsDto(id, document.OcrStatus, facts.Merchant, facts.Date, facts.Total, kind, facts.WarrantyMonths,
            warrantyUntil, matches, complete);
    }

    private async Task<(IReadOnlyList<TransactionMatchDto> Matches, bool Complete)> MatchesAsync(decimal total, DateOnly date,
        HashSet<string> linked, CancellationToken cancellationToken)
    {
        try
        {
            var page = await finance.PostAsync<TransactionWindow, TransactionWindowPage>("/api/Transaction/Filter",
                new TransactionWindow(date.AddDays(-MatchDays).ToDateTime(TimeOnly.MinValue), date.AddDays(MatchDays).ToDateTime(TimeOnly.MinValue)),
                cancellationToken);
            var matches = page.Items
                .Where(t => Math.Abs(Math.Abs(t.Amount) - total) < 0.005m)
                .Select(t =>
                {
                    var day = DateOnly.FromDateTime(t.Date);
                    return new TransactionMatchDto(t.Id, day, t.Description, t.Amount, t.AccountName, Math.Abs(day.DayNumber - date.DayNumber),
                        linked.Contains(t.Id.ToString("D")));
                })
                .OrderBy(m => m.DaysOff).ThenBy(m => m.Date).ThenBy(m => m.Id)
                .Take(MaxMatches)
                .ToList();
            return (matches, true);
        }
        catch (FinanceRequestException exception)
        {
            logger.LogWarning("Documents: the transactions of a receipt could not be read from finance: {Message}", exception.Message);
            return ([], false);
        }
    }
}

/// <summary>The finance transaction filter (finance: TransactionFilterDto) for the days around a receipt.</summary>
internal sealed record TransactionWindow(DateTime StartDate, DateTime EndDate)
{
    public int Page => 1;

    public int PageSize => 100;
}

/// <summary>The part of a finance page of transactions that the match needs.</summary>
internal sealed record TransactionWindowPage(IReadOnlyList<TransactionWindowItem> Items);

internal sealed record TransactionWindowItem(Guid Id, DateTime Date, string Description, decimal Amount, string AccountName);
