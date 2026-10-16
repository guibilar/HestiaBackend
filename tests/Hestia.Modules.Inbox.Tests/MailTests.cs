using System.Collections.Concurrent;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Inbox.Application.Mail;
using Hestia.Modules.Inbox.Contracts;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hestia.Modules.Inbox.Tests;

/// <summary>T102: emails to the Inbox mailbox become notes, and their attachments documents.</summary>
public sealed class MailTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly string _storage = Directory.CreateTempSubdirectory("hestia-inbox-mail-").FullName;

    public void Dispose() => Directory.Delete(_storage, recursive: true);

    /// <summary>A mailbox in memory: the new emails, and the ones marked done.</summary>
    private sealed class FakeMailbox : IInboxMailbox
    {
        public List<InboxMail> New { get; } = [];
        public ConcurrentBag<string> Done { get; } = [];

        public Task<IReadOnlyList<InboxMail>> ReadNewAsync(int max, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InboxMail>>(New.Where(m => !Done.Contains(m.Uid)).Take(max).ToList());

        public Task DoneAsync(IReadOnlyCollection<string> uids, CancellationToken cancellationToken)
        {
            foreach (var uid in uids)
            {
                Done.Add(uid);
            }
            return Task.CompletedTask;
        }
    }

    private HestiaApiFactory Host(FakeMailbox mailbox) => new(
        new Dictionary<string, string?>
        {
            ["Documents:StoragePath"] = _storage,
            ["Documents:Ocr:Enabled"] = "false",
            // The worker stays off; the tests run the reader themselves.
            ["Inbox:Mail:AllowedSenders"] = "me@example.com, @hydro.example"
        },
        services =>
        {
            services.RemoveAll<IInboxMailbox>();
            services.AddSingleton<IInboxMailbox>(mailbox);
        });

    private static Task<InboxMailResult> ReadAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services => services.GetRequiredService<InboxMailReader>().ReadAsync(CancellationToken.None));

    [Fact]
    public async Task AnEmailFromAnAllowedSender_BecomesANote_WithItsAttachmentsAsDocuments()
    {
        var mailbox = new FakeMailbox();
        mailbox.New.Add(new InboxMail("1", "<bill-1@hydro.example>", "bills@hydro.example", "Hydro <bills@hydro.example>",
            "Your October bill", "Amount due: $84.12\nDue on November 5.",
            [new InboxMailAttachment("bill.png", Png), new InboxMailAttachment("terms.docx", [1, 2, 3, 4, 5])]));
        mailbox.New.Add(new InboxMail("2", "<spam@else.example>", "promo@else.example", "Promo", "Buy now", "Cheap!", []));
        await using var factory = Host(mailbox);
        var client = factory.CreateClient();

        var result = await ReadAsync(factory);

        Assert.Equal(new InboxMailResult(Notes: 1, Documents: 1, Ignored: 1, Duplicates: 0), result);
        // Both emails are done: the ignored one is not read again.
        Assert.Equal(["1", "2"], mailbox.Done.Order());
        var note = Assert.Single((await (await client.GetAsync("/api/inbox")).ReadAsync<PaginatedList<InboxItemDto>>()).Items);
        Assert.Equal((InboxSource.Email, "Hydro <bills@hydro.example>"), (note.Source, note.From));
        Assert.StartsWith("Your October bill\n\nAmount due: $84.12\nDue on November 5.\n\nNot added to Documents: terms.docx", note.Text);
        var documentId = Assert.Single(note.DocumentIds!);
        var document = await (await client.GetAsync($"/api/documents/{documentId}")).ReadAsync<DocumentDto>();
        Assert.Equal("bill", document.Title);
        Assert.Equal(["email"], document.Tags);
        Assert.Contains("Your October bill", document.Notes);
    }

    [Fact]
    public async Task TheSameEmailAgain_MakesNoSecondNote()
    {
        var mailbox = new FakeMailbox();
        var mail = new InboxMail("1", "<a@example.com>", "me@example.com", "Me <me@example.com>", "Idea", null, []);
        mailbox.New.Add(mail);
        await using var factory = Host(mailbox);
        await ReadAsync(factory);

        // The move to the done folder failed: the same email comes again with a new uid.
        mailbox.New.Add(mail with { Uid = "7" });
        var again = await ReadAsync(factory);

        Assert.Equal(1, again.Duplicates);
        Assert.Single((await (await factory.CreateClient().GetAsync("/api/inbox")).ReadAsync<PaginatedList<InboxItemDto>>()).Items);
    }

    [Fact]
    public void ANote_HasTheSubject_ThenTheText_AndIsCutToTheLimit()
    {
        var mail = new InboxMail("1", null, "me@example.com", "me@example.com", null, new string('x', 5000), []);

        var text = InboxMailReader.NoteText(mail, []);

        Assert.StartsWith("Email from me@example.com\n\nxxx", text);
        Assert.Equal(InboxLimits.MaxTextLength, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void AMimeMessage_GivesTheSender_TheText_OfAnHtmlBody_AndTheFiles()
    {
        var builder = new BodyBuilder { HtmlBody = "<html><style>p{}</style><body><p>Hello&nbsp;<b>there</b></p><p>Line two</p></body></html>" };
        builder.Attachments.Add("receipt.pdf", [37, 80, 68, 70], new ContentType("application", "pdf"));
        var message = new MimeMessage { Subject = "Receipt", MessageId = "m-1@example.com" };
        message.From.Add(new MailboxAddress("Ana Souza", "Ana@Example.com"));
        message.Body = builder.ToMessageBody();

        var mail = ImapInboxMailbox.Read("9", message);

        Assert.Equal(("9", "m-1@example.com", "Ana@Example.com", "Ana Souza <Ana@Example.com>", "Receipt"),
            (mail.Uid, mail.MessageId, mail.FromAddress, mail.From, mail.Subject));
        Assert.Equal("Hello there\nLine two", mail.Text?.Replace(" ", " "));
        var file = Assert.Single(mail.Attachments);
        Assert.Equal(("receipt.pdf", 4), (file.FileName, file.Content.Length));
    }

    [Theory]
    [InlineData("me@example.com", true)]
    [InlineData("ME@Example.com", true)]
    [InlineData("bills@hydro.example", true)]
    [InlineData("bills@hydro.example.evil", false)]
    [InlineData("you@example.com", false)]
    public void OnlyTheAllowedSenders_AreTaken(string address, bool allowed) =>
        Assert.Equal(allowed, new InboxMailOptions { AllowedSenders = "me@example.com; @hydro.example" }.Allows(address));

    [Fact]
    public void TheMailbox_NeedsItsSettings_AndAllowedSenders_WhenItIsOn()
    {
        Assert.True(new InboxMailOptions().IsValid());
        Assert.False(new InboxMailOptions { Enabled = true, Host = "imap.example.com", Username = "u", Password = "p" }.IsValid());
        Assert.True(new InboxMailOptions { Enabled = true, Host = "imap.example.com", Username = "u", Password = "p", AllowedSenders = "me@example.com" }.IsValid());
    }

    [Fact]
    public async Task ReadsThatFailForAnHour_RaiseAWarning_AndASuccessResolvesIt()
    {
        var state = new InboxMailState();
        var options = new FixedOptions(new InboxMailOptions { Enabled = true, Username = "hestia.inbox@example.com" });
        var alerts = new InboxMailAlerts(state, options);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        var start = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        AlertCheckContext At(DateTimeOffset now) => new(DateOnly.FromDateTime(now.Date), zone, now);

        state.Failed(start, "Authentication failed.");
        Assert.Empty((await alerts.CheckAsync(At(start.AddMinutes(59)), CancellationToken.None)).Alerts);
        var alert = Assert.Single((await alerts.CheckAsync(At(start.AddMinutes(61)), CancellationToken.None)).Alerts);
        Assert.Equal((InboxMailAlerts.Key, AlertSeverity.Warning), (alert.Key, alert.Severity));
        Assert.Equal(AlertValue.Plain("Authentication failed."), alert.Values["error"]);

        state.Succeeded(start.AddMinutes(70));
        var after = await alerts.CheckAsync(At(start.AddMinutes(71)), CancellationToken.None);
        Assert.Empty(after.Alerts);
        Assert.Contains(InboxMailAlerts.Key, after.Covered);
    }

    private sealed class FixedOptions(InboxMailOptions value) : IOptionsMonitor<InboxMailOptions>
    {
        public InboxMailOptions CurrentValue => value;

        public InboxMailOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<InboxMailOptions, string?> listener) => null;
    }
}
