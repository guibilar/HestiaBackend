using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// Checks the expiry alert of one document after a change (T15): a new document, new details (perhaps a new expiry
/// date), or a delete. The Alerts module runs the check and stores it with the event in its inbox (T36), so an event that
/// comes again changes nothing. The OCR text does not change the expiry date, so an OCR change needs no check.
/// </summary>
public sealed class DocumentAlertsConsumer(IAlertChecks alerts)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Documents.DocumentAlertsConsumer";

    public Task Consume(DocumentAdded message, CancellationToken cancellationToken) =>
        CheckAsync(message.DocumentId, message.EventId, cancellationToken);

    public Task Consume(DocumentChanged message, CancellationToken cancellationToken) =>
        message.Change == DocumentChange.Details ? CheckAsync(message.DocumentId, message.EventId, cancellationToken) : Task.CompletedTask;

    public Task Consume(DocumentDeleted message, CancellationToken cancellationToken) =>
        CheckAsync(message.DocumentId, message.EventId, cancellationToken);

    private Task CheckAsync(Guid documentId, Guid eventId, CancellationToken cancellationToken) =>
        alerts.CheckAsync(DocumentExpiryAlerts.SourceName, documentId.ToString(), new AlertTrigger(eventId, InboxName), cancellationToken);
}
