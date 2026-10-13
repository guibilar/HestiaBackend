using System.Net;
using System.Text.Json;
using Hestia.Platform.Errors;
using Hestia.Platform.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hestia.Platform.Tests;

public class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData("/not-found", HttpStatusCode.NotFound, "Not found")]
    [InlineData("/conflict", HttpStatusCode.Conflict, "Conflict")]
    [InlineData("/invalid", HttpStatusCode.BadRequest, null)]
    [InlineData("/bad-request", HttpStatusCode.BadRequest, "Bad Request")]
    [InlineData("/too-large", HttpStatusCode.RequestEntityTooLarge, "Payload Too Large")]
    [InlineData("/unsupported", HttpStatusCode.UnsupportedMediaType, "Unsupported Media Type")]
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

    [Fact]
    public async Task ABadRequest_DoesNotShowTheExceptionMessage()
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
                app.Run(_ => throw new BadHttpRequestException("Failed to read parameter \"Secret secret\" from the request body."));
            });

        var response = await host.GetTestClient().PostAsync("/", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Secret", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A real Kestrel server: Kestrel throws <see cref="BadHttpRequestException"/> (413) when a body is larger than its
    /// limit. The test server has no body size limit.
    /// </summary>
    [Fact]
    public async Task ABodyOverTheKestrelLimit_Is413_WithProblemDetails()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1024)
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(services =>
                {
                    services.AddProblemDetails();
                    services.AddExceptionHandler<ApiExceptionHandler>();
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(async context =>
                    {
                        await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                        await context.Response.WriteAsync("read");
                    });
                }))
            .Build();
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var small = await client.PostAsync("/", new StringContent(new string('a', 512)));
        var large = await client.PostAsync("/", new StringContent(new string('a', 4096)));

        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal("application/problem+json", large.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await large.Content.ReadAsStringAsync());
        Assert.Equal(413, body.RootElement.GetProperty("status").GetInt32());
        await host.StopAsync();
    }

    private static Exception ExceptionFor(PathString path) => path.Value switch
    {
        "/not-found" => new NotFoundException("Item", Guid.Empty),
        "/conflict" => new ConflictException("It exists already."),
        "/invalid" => new InvalidInputException("name", "Enter a name."),
        // Minimal APIs in Development: a body that cannot be read (for example a bad enum value).
        "/bad-request" => new BadHttpRequestException("Failed to read parameter \"UpdateRequest request\" from the request body as JSON."),
        "/too-large" => new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
        "/unsupported" => new BadHttpRequestException("Expected a supported JSON media type.", StatusCodes.Status415UnsupportedMediaType),
        _ => new InvalidOperationException("Something broke.")
    };
}
