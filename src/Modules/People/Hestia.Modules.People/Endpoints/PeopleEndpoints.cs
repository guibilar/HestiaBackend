using Hestia.Modules.People.Application;
using Hestia.Modules.People.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.People.Endpoints;

/// <summary>The People endpoints, under /api/people. A read-only user can call the GET endpoints only.</summary>
public static class PeopleEndpoints
{
    public const string Prefix = "/api/people";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var people = endpoints.MapGroup(Prefix).WithTags("People");

        people.MapGet("/summary", (PersonService service, CancellationToken cancellationToken) => service.SummaryAsync(cancellationToken))
            .WithName("GetPeopleSummary")
            .WithSummary("The person count, the people to reach out to, the birthdays in the next 30 days, and the open loans.");

        people.MapGet("/groups", (PersonService service, CancellationToken cancellationToken) => service.GroupsAsync(cancellationToken))
            .WithName("ListPeopleGroups")
            .WithSummary("The groups in use, by name, with the count of people in each.");

        MapPeople(people);
        MapInteractions(people);
        MapGifts(people);
        MapLoans(people);
    }

    private static void MapPeople(RouteGroupBuilder people)
    {
        people.MapGet("/", (PersonService service, CancellationToken cancellationToken, string? group, string? q, string? sort,
                int page = 1, int pageSize = 20) =>
                service.ListAsync(new PersonQuery(group, q, sort, page, pageSize), cancellationToken))
            .WithName("ListPeople")
            .WithSummary("A page of people, by name (sort=contact: the reach-out day, soonest first).")
            .WithDescription("Filters: group (any letter case), q (words in the name, the nickname, the relationship, the " +
                "groups, the email, the phone, and the notes).");

        people.MapPost("/", async Task<Created<PersonDto>> (CreatePersonRequest request, PersonService service,
                CancellationToken cancellationToken) =>
            {
                var person = await service.CreateAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{person.Person.Id}", person);
            })
            .WithName("CreatePerson")
            .WithSummary("Adds a person. Only the name is required.")
            .WithDescription("birthday is { month, day, year? }. contactEveryDays (1 to 3650) is the reach-out reminder. " +
                "links are { kind, targetId } (Documents.Document, Money.Transaction, Tasks.Task, or Home.Item).");

        people.MapPost("/import", (PeopleImportRequest request, PeopleImport import, CancellationToken cancellationToken) =>
                import.ImportAsync(request, cancellationToken))
            .WithName("ImportPeople")
            .WithSummary("Imports contacts from a vCard file (vCard: its text) or a list (people). dryRun=true (default) gives the preview.")
            .WithDescription("A contact that matches a person (the same email, then the same phone, then the same name) only " +
                "fills the empty details of that person and adds its groups. only: the indexes to import (null: all). At most " +
                "2000 contacts. The answer has each contact with its action: Add, Merge, Same, or Skip (with the reason).");

        people.MapGet("/by-link", (PersonService service, CancellationToken cancellationToken, string? kind, string? id) =>
                service.ListByLinkAsync(kind, id, cancellationToken))
            .WithName("ListPeopleByLink")
            .WithSummary("The people of one thing, for example ?kind=Home.Item&id=<item id>.");

        people.MapGet("/{id:guid}", (Guid id, PersonService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetPerson")
            .WithSummary("One person, with the latest contacts, the gift ideas, the loans, and the links.");

        people.MapPut("/{id:guid}", (Guid id, UpdatePersonRequest request, PersonService service, CancellationToken cancellationToken) =>
                service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdatePerson")
            .WithSummary("Changes the details of a person (all of them). The records and the links stay.");

        people.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, PersonService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeletePerson")
            .WithSummary("Deletes a person with the contacts, the gift ideas, the loans, and the links.");

        people.MapPost("/{id:guid}/links", async Task<Created<PersonLinkDto>> (Guid id, CreatePersonLinkRequest request,
                PersonService service, CancellationToken cancellationToken) =>
            {
                var link = await service.AddLinkAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{id}/links/{link.Id}", link);
            })
            .WithName("AddPersonLink")
            .WithSummary("Links the person to a document, a task, a home item, or a finance transaction.")
            .WithDescription("The host checks that the thing exists. For a transaction it asks finance as the signed-in user (502 when finance does not answer).");

        people.MapDelete("/{id:guid}/links/{linkId:guid}", async Task<NoContent> (Guid id, Guid linkId, PersonService service,
                CancellationToken cancellationToken) =>
            {
                await service.RemoveLinkAsync(id, linkId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RemovePersonLink")
            .WithSummary("Removes a link of the person.");
    }

    private static void MapInteractions(RouteGroupBuilder people)
    {
        people.MapGet("/{id:guid}/interactions", (Guid id, PeopleRecordService service, CancellationToken cancellationToken,
                int page = 1, int pageSize = 20) =>
                service.ListInteractionsAsync(id, page, pageSize, cancellationToken))
            .WithName("ListInteractions")
            .WithSummary("A page of the contacts with a person, newest first.");

        people.MapPost("/{id:guid}/interactions", async Task<Created<InteractionDto>> (Guid id, InteractionRequest request,
                PeopleRecordService service, CancellationToken cancellationToken) =>
            {
                var interaction = await service.LogInteractionAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/interactions/{interaction.Id}", interaction);
            })
            .WithName("LogInteraction")
            .WithSummary("Logs a contact (default: today). It moves the last contact of the person.");

        people.MapPut("/interactions/{id:guid}", (Guid id, InteractionRequest request, PeopleRecordService service,
                CancellationToken cancellationToken) => service.UpdateInteractionAsync(id, request, cancellationToken))
            .WithName("UpdateInteraction")
            .WithSummary("Changes a contact (all values).");

        people.MapDelete("/interactions/{id:guid}", async Task<NoContent> (Guid id, PeopleRecordService service,
                CancellationToken cancellationToken) =>
            {
                await service.DeleteInteractionAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteInteraction")
            .WithSummary("Deletes a contact.");
    }

    private static void MapGifts(RouteGroupBuilder people)
    {
        people.MapPost("/{id:guid}/gifts", async Task<Created<GiftIdeaDto>> (Guid id, GiftIdeaRequest request,
                PeopleRecordService service, CancellationToken cancellationToken) =>
            {
                var gift = await service.AddGiftAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/gifts/{gift.Id}", gift);
            })
            .WithName("AddGiftIdea")
            .WithSummary("Adds a gift idea (with givenOn: a given gift).");

        people.MapPut("/gifts/{id:guid}", (Guid id, GiftIdeaRequest request, PeopleRecordService service,
                CancellationToken cancellationToken) => service.UpdateGiftAsync(id, request, cancellationToken))
            .WithName("UpdateGiftIdea")
            .WithSummary("Changes a gift idea (all values). Set givenOn when the gift was given.");

        people.MapDelete("/gifts/{id:guid}", async Task<NoContent> (Guid id, PeopleRecordService service,
                CancellationToken cancellationToken) =>
            {
                await service.DeleteGiftAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteGiftIdea")
            .WithSummary("Deletes a gift idea.");
    }

    private static void MapLoans(RouteGroupBuilder people)
    {
        people.MapGet("/loans", (PeopleRecordService service, CancellationToken cancellationToken, string? status) =>
                service.ListLoansAsync(status, cancellationToken))
            .WithName("ListLoans")
            .WithSummary("The loans of all people: status=open (default), returned, or all. Open loans by due day first.");

        people.MapPost("/{id:guid}/loans", async Task<Created<LoanDto>> (Guid id, LoanRequest request,
                PeopleRecordService service, CancellationToken cancellationToken) =>
            {
                var loan = await service.AddLoanAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/loans/{loan.Id}", loan);
            })
            .WithName("AddLoan")
            .WithSummary("Adds a thing lent to the person (direction Lent, default) or borrowed from the person (Borrowed).");

        people.MapPut("/loans/{id:guid}", (Guid id, LoanRequest request, PeopleRecordService service,
                CancellationToken cancellationToken) => service.UpdateLoanAsync(id, request, cancellationToken))
            .WithName("UpdateLoan")
            .WithSummary("Changes a loan (all values). Set returnedOn when the thing came back.");

        people.MapDelete("/loans/{id:guid}", async Task<NoContent> (Guid id, PeopleRecordService service,
                CancellationToken cancellationToken) =>
            {
                await service.DeleteLoanAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteLoan")
            .WithSummary("Deletes a loan.");
    }
}
