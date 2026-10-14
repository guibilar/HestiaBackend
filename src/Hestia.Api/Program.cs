using System.Text.Json.Serialization;
using Hestia.Api;
using Hestia.Api.Events;
using Hestia.Api.Gateway;
using Hestia.Platform.Errors;
using Hestia.Platform.Health;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var modules = ModuleLoader.Discover();

builder.Services.ConfigureHttpJsonOptions(options =>
    // Enums by name only: a number would otherwise be accepted as a value that no enum has.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddReverseProxySupport(builder.Configuration);
// The gateway: finance paths go to the finance backend (FinanceProxy section).
builder.Services.AddFinanceProxy(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
// T08: the OpenAPI document of the module endpoints (/openapi/v1.json), for the web and Android clients.
// The finance paths are not in it: finance has its own API description.
builder.Services.AddOpenApi();
// Events (D7): module events in process after commit, and finance events from RabbitMQ (Events section).
builder.AddHestiaEvents(modules);

foreach (var module in modules)
{
    module.AddServices(builder.Services, builder.Configuration);
}

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready])
    .AddCheck<FinanceHealthCheck>("finance", tags: [HealthTags.Ready]);

var app = builder.Build();

app.Logger.LogInformation("Modules: {Modules}", string.Join(", ", modules.Select(module => module.Name)));

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    app.Logger.LogInformation("Applying database migrations");
    await HestiaDatabase.MigrateAsync(app.Services);
}

// A module command (for example "import-finance-user") runs in place of the web server.
if (ModuleCommands.Find(app.Services, args) is { } command)
{
    Environment.ExitCode = await ModuleCommands.RunAsync(app.Services, command, args);
    return;
}

// First, so everything after it sees the scheme and the address that the client used.
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Sign-in (the Identity module, D5): every endpoint needs a signed-in user unless it allows anonymous users.
// The finance proxy routes too: an anonymous request does not reach finance.
app.UseAuthentication();
app.UseAuthorization();
// After the authorization, so only requests that it lets in count towards a limit (the password tries).
app.UseRateLimiter();

// Liveness: the process answers (no dependencies, so a database outage does not restart the host).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
// Readiness: the host can serve requests (each module database and the finance backend answer, and the checks that the
// modules add, for example the documents volume).
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) }).AllowAnonymous();

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

// Needs a signed-in user, as every endpoint (a session cookie, or the read-only token).
app.MapOpenApi();

// After the modules. The finance routes have a lower priority, so a module endpoint on the same path wins.
app.MapFinanceProxy();
// A warning in the log for each module endpoint on a finance path.
FinanceProxy.WarnAboutOverlaps(app);

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program { }
