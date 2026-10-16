using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Application.Receipts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Endpoints;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Platform.Health;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hestia.Modules.Documents;

/// <summary>
/// The Documents module (T11): documents with their files on a volume, background OCR, full-text search, expiry dates,
/// and links to finance transactions.
/// </summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "Documents";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddDocumentsModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapDocumentsModule();
}

public static class DocumentsModuleExtensions
{
    /// <summary>Registers everything the Documents module needs, with one call.</summary>
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DocumentsOptions>()
            .Bind(configuration.GetSection(DocumentsOptions.SectionName))
            // A relative storage path is below the content root of the host.
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                if (!string.IsNullOrWhiteSpace(options.StoragePath))
                {
                    options.StoragePath = Path.GetFullPath(options.StoragePath, environment.ContentRootPath);
                }
            })
            // The shared zone (Hestia:TimeZone, T85), unless Documents:TimeZone overrides it (T86).
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, DocumentsOptions.SectionName))
            .Validate(options => options.IsValid(), DocumentsOptions.ValidationMessage)
            .ValidateOnStart();

        // Durable events from the first migration (T38): Timeline (T16) and Search (T17) depend on them.
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, database => database.UseDurableEvents());
        services.AddSingleton<DocumentStorage>();
        services.AddSingleton<DocumentThumbnails>();
        // T59: /health/ready checks that the host can write to the documents folder.
        services.AddHealthChecks().AddCheck<DocumentStorageHealthCheck>(DocumentStorageHealthCheck.Name, tags: [HealthTags.Ready]);
        // T59, T64: the checks of the volume (unused files, rows without a file), as a command and on a schedule.
        services.AddSingleton<DocumentFileCheck>();
        services.AddSingleton<IModuleCommand, CleanFilesCommand>();
        services.AddSingleton<FileCheckState>();
        services.AddSingleton<FileCheckWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<FileCheckWorker>());
        services.AddScoped<LinkTargets>();
        services.AddScoped<DocumentService>();
        // T100: what the text of a receipt says, and the matching transactions.
        services.AddScoped<DocumentSuggestions>();
        services.AddScoped<IDocumentQueries, DocumentQueries>();

        // The OCR runs in the background (OcrWorker), not in the upload request.
        services.AddSingleton<OcrSignal>();
        services.AddSingleton<IOcrEngine, CommandLineOcrEngine>();
        services.AddScoped<DocumentOcr>();
        services.AddHostedService<OcrWorker>();
        services.AddSingleton<IModuleCommand, RetryOcrCommand>();

        // The expiry rules (T15): the Alerts module runs them, and Today shows their alerts. After a document event,
        // DocumentAlertsConsumer checks that document (Wolverine finds it; it needs no registration).
        services.AddScoped<IAlertSource, DocumentExpiryAlerts>();
        // T80: the rows without a file, from the last scheduled file check (FileCheckWorker).
        services.AddScoped<IAlertSource, DocumentFileAlerts>();
        // The Timeline entries (T16).
        services.AddScoped<ITimelineSource, DocumentTimeline>();
        // The Calendar items (T103).
        services.AddScoped<ICalendarSource, DocumentCalendar>();
        // The search (T17).
        services.AddScoped<ISearchSource, DocumentSearch>();
        return services;
    }

    /// <summary>Maps the Documents endpoints under /api/documents.</summary>
    public static IEndpointRouteBuilder MapDocumentsModule(this IEndpointRouteBuilder endpoints)
    {
        DocumentEndpoints.Map(endpoints);
        return endpoints;
    }
}
