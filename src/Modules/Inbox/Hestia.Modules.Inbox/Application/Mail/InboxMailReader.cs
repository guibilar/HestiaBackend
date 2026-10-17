using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Inbox.Contracts;
using Hestia.Modules.Inbox.Domain;
using Hestia.Modules.Inbox.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Http;
using Hestia.Platform.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Inbox.Application.Mail;

/// <summary>What one read of the mailbox did.</summary>
public sealed record InboxMailResult(int Notes, int Documents, int Ignored, int Duplicates);

/// <summary>
/// Reads the new emails of the Inbox mailbox (T102). An email from an allowed sender becomes an Inbox note (the subject,
/// then the text), and each attachment that Documents takes (PDF, photos) becomes a document with the tag <c>email</c>.
/// An email that made a note already (the same Message-ID) makes no second one. Then the emails are marked done.
/// </summary>
public sealed class InboxMailReader(
    InboxDbContext db,
    IInboxMailbox mailbox,
    IDocumentIntake documents,
    IOptionsMonitor<InboxMailOptions> options,
    TimeProvider time,
    ILogger<InboxMailReader> logger)
{
    /// <summary>The most emails in one read; the next read takes the rest.</summary>
    public const int BatchSize = 25;

    public async Task<InboxMailResult> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var mails = await mailbox.ReadNewAsync(BatchSize, cancellationToken);
        int notes = 0, documentCount = 0, ignored = 0, duplicates = 0;
        var done = new List<string>();
        try
        {
            await ReadAllAsync();
        }
        finally
        {
            // Also after an email that failed: the ones before it are notes now, and an email without a Message-ID would
            // make its note again at each read.
            await mailbox.DoneAsync(done, cancellationToken);
        }
        if (mails.Count > 0)
        {
            logger.LogInformation("Inbox: read {Mails} emails: {Notes} notes, {Documents} documents, {Ignored} not allowed, {Duplicates} already there",
                mails.Count, notes, documentCount, ignored, duplicates);
        }
        return new InboxMailResult(notes, documentCount, ignored, duplicates);

        async Task ReadAllAsync()
        {
            foreach (var received in mails)
            {
                // No NUL characters: PostgreSQL refuses them, and the email would fail at each read.
                var mail = WithoutNul(received);
                if (!settings.Allows(mail.FromAddress))
                {
                    logger.LogWarning("Inbox: an email from {Sender} is not from an allowed sender; it is left alone", mail.FromAddress);
                    ignored++;
                    done.Add(mail.Uid);
                    continue;
                }
                var messageId = Cut(mail.MessageId, InboxLimits.MaxMessageIdLength);
                if (messageId is not null && await db.Items.AnyAsync(i => i.MessageId == messageId, cancellationToken))
                {
                    duplicates++;
                    done.Add(mail.Uid);
                    continue;
                }

                var ids = new List<Guid>();
                var refused = new List<string>();
                foreach (var attachment in mail.Attachments.Where(a => a.Content.Length > 0))
                {
                    try
                    {
                        var document = await documents.AddAsync(new DocumentIntake(null, ["email"],
                            $"From the email \"{mail.Subject}\" ({mail.From}).", [new DocumentIntakeFile(attachment.FileName, attachment.Content)]),
                            cancellationToken);
                        ids.Add(document.Id);
                    }
                    catch (InvalidInputException exception)
                    {
                        // For example a .docx or a calendar invitation: the note says so.
                        refused.Add($"{attachment.FileName} ({exception.Message})");
                    }
                    catch
                    {
                        // No note: the documents of the email go too, or the next read adds them again.
                        await RemoveDocumentsAsync(ids);
                        throw;
                    }
                }

                var now = time.GetUtcNow();
                db.Items.Add(new InboxItem
                {
                    Id = Guid.CreateVersion7(),
                    Text = NoteText(mail, refused),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Source = InboxSource.Email,
                    From = Cut(mail.From, InboxLimits.MaxFromLength),
                    MessageId = messageId,
                    DocumentIds = DocumentIdList.Write(ids)
                });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch
                {
                    await RemoveDocumentsAsync(ids);
                    throw;
                }
                notes++;
                documentCount += ids.Count;
                done.Add(mail.Uid);
            }
        }
    }

    /// <summary>Removes the documents of an email whose note is not saved (a failure is only logged).</summary>
    private async Task RemoveDocumentsAsync(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                await documents.RemoveAsync(id, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Inbox: could not remove the document {DocumentId} of an email that failed", id);
            }
        }
    }

    /// <summary>The email without NUL characters in its texts and file names.</summary>
    public static InboxMail WithoutNul(InboxMail mail) => mail with
    {
        MessageId = NulCharacters.Remove(mail.MessageId),
        FromAddress = NulCharacters.Remove(mail.FromAddress)!,
        From = NulCharacters.Remove(mail.From)!,
        Subject = NulCharacters.Remove(mail.Subject),
        Text = NulCharacters.Remove(mail.Text),
        Attachments = mail.Attachments.Select(a => a with { FileName = NulCharacters.Remove(a.FileName)! }).ToList()
    };

    /// <summary>The subject on the first line, then the text, at most the length of a note.</summary>
    public static string NoteText(InboxMail mail, IReadOnlyList<string> refused)
    {
        var parts = new List<string>();
        var subject = mail.Subject?.Trim();
        parts.Add(string.IsNullOrEmpty(subject) ? $"Email from {mail.From}" : subject);
        if (!string.IsNullOrWhiteSpace(mail.Text))
        {
            parts.Add(mail.Text.Trim());
        }
        if (refused.Count > 0)
        {
            parts.Add("Not added to Documents: " + string.Join("; ", refused));
        }
        var text = string.Join("\n\n", parts);
        return text.Length <= InboxLimits.MaxTextLength ? text : TextCut.Start(text, InboxLimits.MaxTextLength - 1) + "…";
    }

    private static string? Cut(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : TextCut.Start(text.Trim(), max);
}
