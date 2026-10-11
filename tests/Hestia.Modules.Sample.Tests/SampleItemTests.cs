using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Sample.Tests;

public class SampleItemTests
{
    private const string Items = "/api/sample/items";

    [Fact]
    public async Task Create_ReturnsTheItem_AndTheListShowsIt()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("  Kettle  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsync<SampleItemDto>();
        Assert.Equal("Kettle", created.Name);
        Assert.Equal($"{Items}/{created.Id}", response.Headers.Location?.OriginalString);

        var list = await (await client.GetAsync(Items)).ReadAsync<PaginatedList<SampleItemDto>>();
        var item = Assert.Single(list.Items);
        Assert.Equal(created.Id, item.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_WithoutAName_IsABadRequest_ForTheNameField(string? name)
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Items, new CreateSampleItemRequest(name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<HttpValidationProblemDetails>();
        Assert.Contains("name", problem.Errors);
    }

    [Fact]
    public async Task Create_WithANameThatExists_IsAConflict()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));

        var response = await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsPages_InNameOrder()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        foreach (var name in new[] { "Cup", "Apron", "Broom" })
        {
            await client.PostAsJsonAsync(Items, new CreateSampleItemRequest(name));
        }

        var page = await (await client.GetAsync($"{Items}?page=2&pageSize=2")).ReadAsync<PaginatedList<SampleItemDto>>();

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal("Cup", Assert.Single(page.Items).Name);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_WithAPageOutOfRange_IsABadRequest(string query)
    {
        await using var factory = new HestiaApiFactory();

        var response = await factory.CreateClient().GetAsync($"{Items}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OtherModules_ReadItems_ThroughTheQueryInterface()
    {
        await using var factory = new HestiaApiFactory();
        var created = await (await factory.CreateClient().PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle")))
            .ReadAsync<SampleItemDto>();

        var found = await factory.WithScopeAsync(services =>
            services.GetRequiredService<ISampleItemQueries>().FindAsync(created.Id));
        var missing = await factory.WithScopeAsync(services =>
            services.GetRequiredService<ISampleItemQueries>().FindAsync(Guid.NewGuid()));

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("Kettle", found.Name);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Migrations_PutTheTablesAndTheirHistory_InTheSampleSchema()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();

        var tables = await factory.WithScopeAsync(services =>
            services.GetRequiredService<SampleDbContext>().Database
                .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'sample'")
                .ToListAsync());

        Assert.Contains("Items", tables);
        Assert.Contains("__EFMigrationsHistory", tables);
    }
}
