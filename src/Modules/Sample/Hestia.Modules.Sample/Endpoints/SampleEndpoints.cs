using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Sample.Endpoints;

/// <summary>Each module maps its endpoints under its own prefix, here /api/sample.</summary>
public static class SampleEndpoints
{
    public const string Prefix = "/api/sample";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapGroup($"{Prefix}/items");

        items.MapGet("/", (SampleItemService service, CancellationToken cancellationToken, int page = 1, int pageSize = 20) =>
            service.ListAsync(page, pageSize, cancellationToken));

        items.MapPost("/", async (CreateSampleItemRequest request, SampleItemService service, CancellationToken cancellationToken) =>
        {
            var item = await service.CreateAsync(request, cancellationToken);
            return Results.Created($"{Prefix}/items/{item.Id}", item);
        });

        // What the event consumer saw: SampleItemCreated reaches it after each item is committed.
        endpoints.MapGet($"{Prefix}/activity", (SampleActivity activity) => activity.Read());
    }
}
