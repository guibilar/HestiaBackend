using Hestia.Modules.Inbox.Application;
using Hestia.Modules.Inbox.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Inbox.Endpoints;

/// <summary>The Inbox endpoints, under /api/inbox. A read-only user can call the GET endpoints only.</summary>
public static class InboxEndpoints
{
    public const string Prefix = "/api/inbox";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var inbox = endpoints.MapGroup(Prefix).WithTags("Inbox");

        inbox.MapGet("/", (InboxService service, CancellationToken cancellationToken, bool done = false, string? q = null,
                int page = 1, int pageSize = 50) =>
                service.ListAsync(done, q, page, pageSize, cancellationToken))
            .WithName("ListInbox")
            .WithSummary("The notes to sort, newest first (done=true: the sorted ones, last sorted first).")
            .WithDescription("q: words in the text.");

        inbox.MapPost("/", async Task<Created<InboxItemDto>> (InboxTextRequest request, InboxService service,
                CancellationToken cancellationToken) =>
            {
                var item = await service.AddAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{item.Id}", item);
            })
            .WithName("AddInboxNote")
            .WithSummary("Adds a note (at most 4000 characters).");

        inbox.MapGet("/{id:guid}", (Guid id, InboxService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetInboxNote");

        inbox.MapPut("/{id:guid}", (Guid id, InboxTextRequest request, InboxService service, CancellationToken cancellationToken) =>
                service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateInboxNote")
            .WithSummary("Changes the text of a note.");

        inbox.MapPost("/{id:guid}/done", (Guid id, InboxDoneRequest? request, InboxService service, CancellationToken cancellationToken) =>
                service.DoneAsync(id, request ?? new InboxDoneRequest(), cancellationToken))
            .WithName("SortInboxNote")
            .WithSummary("Marks a note sorted, with where it went (sortedTo, for example Task). The client adds the thing first.");

        inbox.MapPost("/{id:guid}/reopen", (Guid id, InboxService service, CancellationToken cancellationToken) =>
                service.ReopenAsync(id, cancellationToken))
            .WithName("ReopenInboxNote")
            .WithSummary("Puts a sorted note back with the open ones (an undo).");

        inbox.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, InboxService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteInboxNote");
    }
}
