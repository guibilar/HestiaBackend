using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hestia.Modules.Inbox.Application.Mail;

/// <summary>One email of the Inbox mailbox, read.</summary>
/// <param name="Uid">Its id in the folder, to mark it done.</param>
/// <param name="MessageId">Its Message-ID, or null.</param>
/// <param name="FromAddress">The sender's address.</param>
/// <param name="From">The sender as shown (name and address).</param>
/// <param name="Subject">The subject, or null.</param>
/// <param name="Text">The text of the body (the plain part, or the HTML part as text), or null.</param>
/// <param name="Attachments">The attached files.</param>
public sealed record InboxMail(
    string Uid,
    string? MessageId,
    string FromAddress,
    string From,
    string? Subject,
    string? Text,
    IReadOnlyList<InboxMailAttachment> Attachments);

public sealed record InboxMailAttachment(string FileName, byte[] Content);

/// <summary>The mailbox of the Inbox (T102). Tests replace it.</summary>
public interface IInboxMailbox
{
    /// <summary>The emails that are not read yet, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<InboxMail>> ReadNewAsync(int max, CancellationToken cancellationToken);

    /// <summary>Marks the emails as read, and moves them to the done folder when there is one.</summary>
    Task DoneAsync(IReadOnlyCollection<string> uids, CancellationToken cancellationToken);
}

/// <summary>The mailbox over IMAP with MailKit. Each call opens its own connection: a poll every few minutes needs no more.</summary>
public sealed class ImapInboxMailbox(IOptionsMonitor<InboxMailOptions> options) : IInboxMailbox
{
    public async Task<IReadOnlyList<InboxMail>> ReadNewAsync(int max, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(cancellationToken);
        var folder = await client.GetFolderAsync(options.CurrentValue.Folder, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var uids = (await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken)).OrderBy(uid => uid.Id).Take(max).ToList();
        var mails = new List<InboxMail>();
        foreach (var uid in uids)
        {
            var message = await folder.GetMessageAsync(uid, cancellationToken);
            mails.Add(Read(uid.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), message));
        }
        await client.DisconnectAsync(true, cancellationToken);
        return mails;
    }

    public async Task DoneAsync(IReadOnlyCollection<string> uids, CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
        {
            return;
        }
        using var client = await ConnectAsync(cancellationToken);
        var settings = options.CurrentValue;
        var folder = await client.GetFolderAsync(settings.Folder, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var ids = uids.Select(uid => new UniqueId(uint.Parse(uid, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await folder.AddFlagsAsync(ids, MessageFlags.Seen, silent: true, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.DoneFolder))
        {
            var root = client.GetFolder(client.PersonalNamespaces[0]);
            IMailFolder? done;
            try
            {
                done = await client.GetFolderAsync(settings.DoneFolder, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
                done = await root.CreateAsync(settings.DoneFolder, isMessageFolder: true, cancellationToken);
            }
            if (done is null)
            {
                throw new InvalidOperationException($"The folder {settings.DoneFolder} could not be made.");
            }
            await folder.MoveToAsync(ids, done, cancellationToken);
        }
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(settings.Host!, settings.Port,
                settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
            await client.AuthenticateAsync(settings.Username!, settings.Password!, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The parts of a MIME message that the Inbox keeps.</summary>
    public static InboxMail Read(string uid, MimeMessage message)
    {
        var sender = message.From.Mailboxes.FirstOrDefault() ?? message.Sender;
        var address = sender?.Address ?? string.Empty;
        var shown = sender is null ? string.Empty : string.IsNullOrWhiteSpace(sender.Name) ? address : $"{sender.Name} <{address}>";
        var text = message.TextBody ?? (message.HtmlBody is { } html ? HtmlText.ToText(html) : null);
        var attachments = new List<InboxMailAttachment>();
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            attachments.Add(new InboxMailAttachment(part.FileName ?? "attachment", stream.ToArray()));
        }
        // Photos pasted in the body (inline images, for example from a phone) are attachments too.
        foreach (var part in message.BodyParts.OfType<MimePart>().Where(p => !p.IsAttachment && p.ContentType.MediaType == "image"))
        {
            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            attachments.Add(new InboxMailAttachment(part.FileName ?? $"image.{part.ContentType.MediaSubtype}", stream.ToArray()));
        }
        return new InboxMail(uid, message.MessageId, address, shown, message.Subject, text, attachments);
    }
}

/// <summary>The text of an HTML body: the tags out, the line breaks kept.</summary>
public static partial class HtmlText
{
    [System.Text.RegularExpressions.GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Hidden();

    [System.Text.RegularExpressions.GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h\d)\s*/?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Breaks();

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex Tags();

    [System.Text.RegularExpressions.GeneratedRegex(@"[ \t]+")]
    private static partial System.Text.RegularExpressions.Regex Spaces();

    [System.Text.RegularExpressions.GeneratedRegex(@"\n\s*\n+")]
    private static partial System.Text.RegularExpressions.Regex EmptyLines();

    public static string ToText(string html)
    {
        var text = Breaks().Replace(Hidden().Replace(html, string.Empty), "\n");
        text = System.Net.WebUtility.HtmlDecode(Tags().Replace(text, string.Empty));
        text = Spaces().Replace(text, " ");
        return EmptyLines().Replace(string.Join("\n", text.Split('\n').Select(l => l.Trim())), "\n\n").Trim();
    }
}
