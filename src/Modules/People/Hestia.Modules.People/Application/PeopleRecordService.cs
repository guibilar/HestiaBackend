using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Application;

/// <summary>The records of a person: contacts, gift ideas, and loans. A contact moves the last contact of the person.</summary>
public sealed class PeopleRecordService(PeopleDbContext db, PeopleReader reader, PeopleDays days)
{
    // ---- Contacts ----

    public async Task<PaginatedList<InteractionDto>> ListInteractionsAsync(Guid personId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(page, pageSize);
        await db.PersonAsync(personId, cancellationToken);
        var interactions = db.Interactions.AsNoTracking().Where(i => i.PersonId == personId);
        var total = await interactions.CountAsync(cancellationToken);
        var items = await interactions
            .OrderByDescending(i => i.OccurredOn).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PaginatedList<InteractionDto>
        {
            Items = items.Select(PeopleReader.Interaction).ToList(), Page = page, PageSize = pageSize, TotalCount = total
        };
    }

    public async Task<InteractionDto> LogInteractionAsync(Guid personId, InteractionRequest request, CancellationToken cancellationToken)
    {
        var person = await db.PersonAsync(personId, cancellationToken);
        var now = days.Now;
        var interaction = new Interaction { Id = Guid.CreateVersion7(), PersonId = personId, CreatedAt = now };
        Apply(interaction, request);
        db.Interactions.Add(interaction);
        await db.RefreshLastContactAsync(person, cancellationToken);
        db.RaiseEvent(InteractionEvent(interaction, person, PeopleRecordChange.Added, now));
        await db.SaveChangesAsync(cancellationToken);
        return PeopleReader.Interaction(interaction);
    }

    public async Task<InteractionDto> UpdateInteractionAsync(Guid id, InteractionRequest request, CancellationToken cancellationToken)
    {
        var interaction = await db.Interactions.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Interaction", id);
        var person = await db.PersonAsync(interaction.PersonId, cancellationToken);
        Apply(interaction, request);
        await db.RefreshLastContactAsync(person, cancellationToken);
        db.RaiseEvent(InteractionEvent(interaction, person, PeopleRecordChange.Changed, days.Now));
        await db.SaveChangesAsync(cancellationToken);
        return PeopleReader.Interaction(interaction);
    }

    public async Task DeleteInteractionAsync(Guid id, CancellationToken cancellationToken)
    {
        var interaction = await db.Interactions.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Interaction", id);
        var person = await db.PersonAsync(interaction.PersonId, cancellationToken);
        db.Interactions.Remove(interaction);
        await db.RefreshLastContactAsync(person, cancellationToken);
        db.RaiseEvent(InteractionEvent(interaction, person, PeopleRecordChange.Deleted, days.Now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    private void Apply(Interaction interaction, InteractionRequest request)
    {
        var today = days.Today;
        var day = request.OccurredOn ?? today;
        if (day > today)
        {
            throw new InvalidInputException("occurredOn", "A contact cannot be in the future.");
        }
        interaction.OccurredOn = day;
        interaction.Kind = PeopleText.Line(request.Kind, "kind", Interaction.MaxKindLength);
        interaction.Note = PeopleText.Block(request.Note, "note", Interaction.MaxNoteLength);
    }

    private static InteractionChanged InteractionEvent(Interaction interaction, Person person, PeopleRecordChange change, DateTimeOffset now) =>
        new(interaction.Id, person.Id, person.Name, change, interaction.OccurredOn, interaction.Kind, now);

    // ---- Gift ideas ----

    public async Task<GiftIdeaDto> AddGiftAsync(Guid personId, GiftIdeaRequest request, CancellationToken cancellationToken)
    {
        var person = await db.PersonAsync(personId, cancellationToken);
        var now = days.Now;
        var gift = new GiftIdea { Id = Guid.CreateVersion7(), PersonId = personId, CreatedAt = now, UpdatedAt = now };
        Apply(gift, request);
        db.Gifts.Add(gift);
        db.RaiseEvent(GiftEvent(gift, person, PeopleRecordChange.Added, now));
        await db.SaveChangesAsync(cancellationToken);
        return PeopleReader.Gift(gift);
    }

    public async Task<GiftIdeaDto> UpdateGiftAsync(Guid id, GiftIdeaRequest request, CancellationToken cancellationToken)
    {
        var gift = await db.Gifts.FirstOrDefaultAsync(g => g.Id == id, cancellationToken) ?? throw new NotFoundException("Gift", id);
        var person = await db.PersonAsync(gift.PersonId, cancellationToken);
        var now = days.Now;
        Apply(gift, request);
        gift.UpdatedAt = now;
        db.RaiseEvent(GiftEvent(gift, person, PeopleRecordChange.Changed, now));
        await db.SaveChangesAsync(cancellationToken);
        return PeopleReader.Gift(gift);
    }

    public async Task DeleteGiftAsync(Guid id, CancellationToken cancellationToken)
    {
        var gift = await db.Gifts.FirstOrDefaultAsync(g => g.Id == id, cancellationToken) ?? throw new NotFoundException("Gift", id);
        var person = await db.PersonAsync(gift.PersonId, cancellationToken);
        db.Gifts.Remove(gift);
        db.RaiseEvent(GiftEvent(gift, person, PeopleRecordChange.Deleted, days.Now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    private void Apply(GiftIdea gift, GiftIdeaRequest request)
    {
        if (request.GivenOn > days.Today)
        {
            throw new InvalidInputException("givenOn", "A gift cannot be given in the future. Leave the day empty for an idea.");
        }
        gift.Title = PeopleText.Required(request.Title, "title", GiftIdea.MaxTitleLength, "Enter the gift idea.");
        gift.Notes = PeopleText.Block(request.Notes, "notes", GiftIdea.MaxNotesLength);
        gift.Price = PeopleText.Amount(request.Price, "price");
        gift.Occasion = PeopleText.Line(request.Occasion, "occasion", GiftIdea.MaxOccasionLength);
        gift.GivenOn = request.GivenOn;
    }

    private static GiftChanged GiftEvent(GiftIdea gift, Person person, PeopleRecordChange change, DateTimeOffset now) =>
        new(gift.Id, person.Id, person.Name, change, gift.Title, gift.Price, gift.GivenOn, now);

    // ---- Loans ----

    /// <summary>The loans of all people: open (default) after their due day first, returned, or all.</summary>
    public async Task<List<LoanDto>> ListLoansAsync(string? status, CancellationToken cancellationToken)
    {
        IQueryable<Loan> loans = db.Loans.AsNoTracking();
        loans = (status?.Trim().ToLowerInvariant() ?? "open") switch
        {
            "" or "open" => loans.Where(l => l.ReturnedOn == null),
            "returned" => loans.Where(l => l.ReturnedOn != null),
            "all" => loans,
            _ => throw new InvalidInputException("status", "Use status=open, returned, or all.")
        };
        var rows = await loans
            .Select(l => new { Loan = l, PersonName = db.People.Where(p => p.Id == l.PersonId).Select(p => p.Name).First() })
            .ToListAsync(cancellationToken);
        var today = days.Today;
        return rows.Select(r => PeopleReader.Loan(r.Loan, r.PersonName, today))
            .OrderBy(l => l.ReturnedOn is not null)
            .ThenBy(l => l.ReturnedOn is null ? (l.DueOn ?? DateOnly.MaxValue).DayNumber : -l.ReturnedOn.Value.DayNumber)
            .ThenBy(l => l.LentOn)
            .ThenBy(l => l.Id)
            .ToList();
    }

    public async Task<LoanDto> AddLoanAsync(Guid personId, LoanRequest request, CancellationToken cancellationToken)
    {
        var person = await db.PersonAsync(personId, cancellationToken);
        var now = days.Now;
        var loan = new Loan { Id = Guid.CreateVersion7(), PersonId = personId, CreatedAt = now, UpdatedAt = now };
        Apply(loan, request);
        db.Loans.Add(loan);
        db.RaiseEvent(LoanEvent(loan, person, PeopleRecordChange.Added, now));
        await db.SaveChangesAsync(cancellationToken);
        return reader.Loan(loan, person.Name);
    }

    public async Task<LoanDto> UpdateLoanAsync(Guid id, LoanRequest request, CancellationToken cancellationToken)
    {
        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == id, cancellationToken) ?? throw new NotFoundException("Loan", id);
        var person = await db.PersonAsync(loan.PersonId, cancellationToken);
        var now = days.Now;
        Apply(loan, request);
        loan.UpdatedAt = now;
        db.RaiseEvent(LoanEvent(loan, person, PeopleRecordChange.Changed, now));
        await db.SaveChangesAsync(cancellationToken);
        return reader.Loan(loan, person.Name);
    }

    public async Task DeleteLoanAsync(Guid id, CancellationToken cancellationToken)
    {
        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == id, cancellationToken) ?? throw new NotFoundException("Loan", id);
        var person = await db.PersonAsync(loan.PersonId, cancellationToken);
        db.Loans.Remove(loan);
        db.RaiseEvent(LoanEvent(loan, person, PeopleRecordChange.Deleted, days.Now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    private void Apply(Loan loan, LoanRequest request)
    {
        var today = days.Today;
        var lentOn = request.LentOn ?? today;
        if (lentOn > today)
        {
            throw new InvalidInputException("lentOn", "A loan cannot start in the future.");
        }
        if (request.DueOn < lentOn)
        {
            throw new InvalidInputException("dueOn", "The due day cannot be before the day of the loan.");
        }
        if (request.ReturnedOn is { } returned && (returned < lentOn || returned > today))
        {
            throw new InvalidInputException("returnedOn", "The return day must be from the day of the loan to today.");
        }
        if (request.Direction is { } direction && !Enum.IsDefined(direction))
        {
            throw new InvalidInputException("direction", "Use Lent or Borrowed.");
        }
        loan.ItemName = PeopleText.Required(request.ItemName, "itemName", Loan.MaxItemNameLength, "Enter the thing.");
        loan.Direction = request.Direction ?? LoanDirection.Lent;
        loan.LentOn = lentOn;
        loan.DueOn = request.DueOn;
        loan.ReturnedOn = request.ReturnedOn;
        loan.Notes = PeopleText.Block(request.Notes, "notes", Loan.MaxNotesLength);
    }

    private static LoanChanged LoanEvent(Loan loan, Person person, PeopleRecordChange change, DateTimeOffset now) =>
        new(loan.Id, person.Id, person.Name, change, loan.ItemName, loan.Direction, loan.LentOn, loan.DueOn, loan.ReturnedOn, now);
}
