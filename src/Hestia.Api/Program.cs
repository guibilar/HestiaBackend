using System.Text.Json.Serialization;
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
// Events (D7): module events in process after commit, and finance events from RabbitMQ (Events section).
builder.AddHestiaEvents(modules);

foreach (var module in modules)
{
    module.AddServices(builder.Services, builder.Configuration);
}

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
    .AddCheck<FinanceHealthCheck>("finance", tags: [ReadyTag]);

var app = builder.Build();

app.Logger.LogInformation("Modules: {Modules}", string.Join(", ", modules.Select(module => module.Name)));

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    app.Logger.LogInformation("Applying database migrations");
    await HestiaDatabase.MigrateAsync(app.Services);
}

// First, so everything after it sees the scheme and the address that the client used.
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness: the process answers (no dependencies, so a database outage does not restart the host).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: the host can serve requests (each module database and the finance backend answer).
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

// After the modules. The finance routes have a lower priority, so a module endpoint on the same path wins.
app.MapFinanceProxy();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program
{
    private const string ReadyTag = "ready";
}
