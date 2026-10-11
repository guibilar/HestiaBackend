using System.Net;
using System.Text.Json;
using Hestia.Platform.Errors;
using Hestia.Platform.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Platform.Tests;

public class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData("/not-found", HttpStatusCode.NotFound, "Not found")]
    [InlineData("/conflict", HttpStatusCode.Conflict, "Conflict")]
    [InlineData("/invalid", HttpStatusCode.BadRequest, null)]
    [InlineData("/other", HttpStatusCode.InternalServerError, "An unexpected error occurred")]
    public async Task Exceptions_BecomeProblemDetails(string path, HttpStatusCode status, string? title)
    {
        using var host = await WebTestServer.StartAsync(
            services =>
            {
                services.AddProblemDetails();
                services.AddExceptionHandler<ApiExceptionHandler>();
            },
            app =>
            {
                app.UseExceptionHandler();
                app.Run(context => throw ExceptionFor(context.Request.Path));
            });

        var response = await host.GetTestClient().GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (title is not null)
        {
            Assert.Equal(title, body.RootElement.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task InvalidInput_PutsTheMessageUnderTheField()
    {
        using var host = await WebTestServer.StartAsync(
            services =>
            {
                services.AddProblemDetails();
                services.AddExceptionHandler<ApiExceptionHandler>();
            },
            app =>
            {
                app.UseExceptionHandler();
                app.Run(_ => throw new InvalidInputException("name", "Enter a name."));
            });

        var response = await host.GetTestClient().GetAsync("/");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var messages = body.RootElement.GetProperty("errors").GetProperty("name");
        Assert.Equal("Enter a name.", Assert.Single(messages.EnumerateArray()).GetString());
    }

    private static Exception ExceptionFor(PathString path) => path.Value switch
    {
        "/not-found" => new NotFoundException("Item", Guid.Empty),
        "/conflict" => new ConflictException("It exists already."),
        "/invalid" => new InvalidInputException("name", "Enter a name."),
        _ => new InvalidOperationException("Something broke.")
    };
}
