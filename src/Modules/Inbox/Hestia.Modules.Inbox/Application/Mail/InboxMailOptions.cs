namespace Hestia.Modules.Inbox.Application.Mail;

/// <summary>
/// The Inbox mailbox (section <c>Inbox:Mail</c>, T102): Hestia reads a mailbox that only it uses, and each email from an
/// allowed sender becomes an Inbox note, with its attachments as documents. Off until <see cref="Enabled"/> is true.
/// </summary>
public sealed class InboxMailOptions
{
    public const string SectionName = "Inbox:Mail";

    public bool Enabled { get; set; }

    /// <summary>The IMAP server, for example <c>imap.gmail.com</c>.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 993;

    /// <summary>True: TLS from the start (port 993). False: STARTTLS when the server offers it.</summary>
    public bool UseSsl { get; set; } = true;

    public string? Username { get; set; }

    /// <summary>The password, for Gmail an app password. Keep it in the .env, never in a file in git.</summary>
    public string? Password { get; set; }

    /// <summary>The folder to read.</summary>
    public string Folder { get; set; } = "INBOX";

    /// <summary>The folder for the emails that Hestia has read (made when it is missing). Empty: they stay, marked as read.</summary>
    public string? DoneFolder { get; set; } = "Hestia";

    /// <summary>
    /// The addresses (or <c>@domain</c>) whose emails Hestia takes, separated by commas. Anyone can send to the mailbox: an
    /// email from another sender is marked as read and left alone. Required when the mailbox is on.
    /// </summary>
    public string? AllowedSenders { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The alert "Cannot read the Inbox mailbox" comes after the reads failed for this long.</summary>
    public TimeSpan AlertAfter { get; set; } = TimeSpan.FromHours(1);

    public IReadOnlyList<string> Senders =>
        (AllowedSenders ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToList();

    /// <summary>True when the sender's address is allowed: the same address, or the same domain for an <c>@domain</c> entry.</summary>
    public bool Allows(string address)
    {
        var lower = address.Trim().ToLowerInvariant();
        return Senders.Any(s => s.StartsWith('@') ? lower.EndsWith(s, StringComparison.Ordinal) : lower == s);
    }

    public bool IsValid() =>
        !Enabled || (!string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password)
            && Senders.Count > 0 && Port is > 0 and < 65536 && PollInterval >= TimeSpan.FromSeconds(10));

    public const string ValidationMessage =
        "Inbox:Mail: with Enabled=true, set Host, Username, Password, and AllowedSenders (the addresses whose emails Hestia takes).";
}
