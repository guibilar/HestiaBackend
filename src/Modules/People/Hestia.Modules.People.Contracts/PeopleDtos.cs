namespace Hestia.Modules.People.Contracts;

/// <summary>A birthday: the month and the day, and the year when the user knows it.</summary>
/// <param name="Month">1 to 12.</param>
/// <param name="Day">1 to 31 (a day that the month can have; February 29 is allowed).</param>
/// <param name="Year">The year of birth, or null.</param>
public sealed record BirthdayDto(int Month, int Day, int? Year = null);

/// <summary>A link from a person to something outside the People module, for example a document or a home item.</summary>
public sealed record PersonLinkDto(Guid Id, string Kind, string TargetId, DateTimeOffset CreatedAt);

/// <summary>
/// A document or a task that links to the person (kind <c>People.Person</c> in that module, T93). The link belongs to the
/// document or the task: it is removed there, not on the person.
/// </summary>
/// <param name="Kind"><c>Documents.Document</c> or <c>Tasks.Task</c>.</param>
/// <param name="TargetId">The id of the document or the task.</param>
/// <param name="Title">The title of the document or the task.</param>
public sealed record PersonLinkedFromDto(string Kind, string TargetId, string Title);

/// <summary>A person in a list.</summary>
/// <param name="Id">The person.</param>
/// <param name="Name">The name.</param>
/// <param name="Nickname">A short name, or null.</param>
/// <param name="Relationship">Free text, for example "Friend", or null.</param>
/// <param name="Groups">The groups, in lower case (for example <c>family</c>, <c>gaming group</c>).</param>
/// <param name="Birthday">The birthday, or null.</param>
/// <param name="NextBirthday">The next birthday (today or later), or null. February 29 is February 28 in other years.</param>
/// <param name="Email">The email address, or null.</param>
/// <param name="Phone">The phone number, or null.</param>
/// <param name="ContactEveryDays">The reach-out reminder in days, or null for no reminder.</param>
/// <param name="LastContactOn">The day of the latest contact, or null.</param>
/// <param name="ContactDueOn">
/// With a reminder: the day to reach out (the last contact, or the day the person was added, plus the reminder). Else null.
/// </param>
/// <param name="ContactOverdue">True when <paramref name="ContactDueOn"/> is before today.</param>
/// <param name="CreatedAt">When the person was added.</param>
/// <param name="UpdatedAt">When the person last changed.</param>
public sealed record PersonSummaryDto(
    Guid Id,
    string Name,
    string? Nickname,
    string? Relationship,
    IReadOnlyList<string> Groups,
    BirthdayDto? Birthday,
    DateOnly? NextBirthday,
    string? Email,
    string? Phone,
    int? ContactEveryDays,
    DateOnly? LastContactOn,
    DateOnly? ContactDueOn,
    bool ContactOverdue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A contact with a person: a call, a coffee, a visit.</summary>
/// <param name="Id">The contact.</param>
/// <param name="PersonId">The person.</param>
/// <param name="OccurredOn">The day of the contact.</param>
/// <param name="Kind">Free text, for example "Call", or null.</param>
/// <param name="Note">What happened, or null.</param>
/// <param name="CreatedAt">When it was logged.</param>
public sealed record InteractionDto(Guid Id, Guid PersonId, DateOnly OccurredOn, string? Kind, string? Note, DateTimeOffset CreatedAt);

/// <summary>A gift idea for a person. With <paramref name="GivenOn"/>, the gift was given.</summary>
/// <param name="Id">The gift idea.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Title">What the gift is.</param>
/// <param name="Notes">Free text (for example a link), or null.</param>
/// <param name="Price">The price (CAD, cents), or null.</param>
/// <param name="Occasion">Free text, for example "Birthday", or null.</param>
/// <param name="GivenOn">The day the gift was given, or null for an idea.</param>
/// <param name="CreatedAt">When it was added.</param>
public sealed record GiftIdeaDto(
    Guid Id,
    Guid PersonId,
    string Title,
    string? Notes,
    decimal? Price,
    string? Occasion,
    DateOnly? GivenOn,
    DateTimeOffset CreatedAt);

/// <summary>Who has the thing of a loan.</summary>
public enum LoanDirection
{
    /// <summary>The user lent the thing to the person.</summary>
    Lent,

    /// <summary>The user borrowed the thing from the person.</summary>
    Borrowed
}

/// <summary>A thing lent to a person, or borrowed from a person.</summary>
/// <param name="Id">The loan.</param>
/// <param name="PersonId">The person.</param>
/// <param name="PersonName">The name of the person.</param>
/// <param name="ItemName">The thing, for example "Camping stove".</param>
/// <param name="Direction"><c>Lent</c> or <c>Borrowed</c>.</param>
/// <param name="LentOn">The day of the loan.</param>
/// <param name="DueOn">The day the thing should come back, or null.</param>
/// <param name="ReturnedOn">The day the thing came back, or null while the loan is open.</param>
/// <param name="Overdue">True for an open loan after its due day.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="CreatedAt">When it was added.</param>
public sealed record LoanDto(
    Guid Id,
    Guid PersonId,
    string PersonName,
    string ItemName,
    LoanDirection Direction,
    DateOnly LentOn,
    DateOnly? DueOn,
    DateOnly? ReturnedOn,
    bool Overdue,
    string? Notes,
    DateTimeOffset CreatedAt);

/// <summary>One person, with the contacts, the gift ideas, the loans, and the links (from both sides).</summary>
/// <param name="Person">The person.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="Interactions">The latest contacts (at most 20), newest first. All: <c>GET /api/people/{id}/interactions</c>.</param>
/// <param name="InteractionCount">How many contacts the person has.</param>
/// <param name="GiftIdeas">The ideas first (oldest first), then the given gifts (newest first).</param>
/// <param name="Loans">The open loans first (oldest first), then the returned ones (newest first).</param>
/// <param name="Links">The links, oldest first.</param>
/// <param name="LinkedFrom">The documents and the tasks that link to the person (T93): the documents first, each group by title.</param>
public sealed record PersonDto(
    PersonSummaryDto Person,
    string? Notes,
    IReadOnlyList<InteractionDto> Interactions,
    int InteractionCount,
    IReadOnlyList<GiftIdeaDto> GiftIdeas,
    IReadOnlyList<LoanDto> Loans,
    IReadOnlyList<PersonLinkDto> Links,
    IReadOnlyList<PersonLinkedFromDto> LinkedFrom);

/// <summary>A new person (<c>POST /api/people</c>). Only the name is required.</summary>
/// <param name="Name">The name (at most 200 characters).</param>
/// <param name="Nickname">A short name.</param>
/// <param name="Relationship">Free text, for example "Friend" or "Sister".</param>
/// <param name="Groups">The groups (each value can hold several, with commas).</param>
/// <param name="Birthday">The birthday.</param>
/// <param name="Email">The email address.</param>
/// <param name="Phone">The phone number.</param>
/// <param name="Notes">Free text.</param>
/// <param name="ContactEveryDays">A reach-out reminder in days (1 to 3650), or null.</param>
/// <param name="Links">Links to make at once.</param>
public sealed record CreatePersonRequest(
    string? Name,
    string? Nickname = null,
    string? Relationship = null,
    IReadOnlyList<string>? Groups = null,
    BirthdayDto? Birthday = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    int? ContactEveryDays = null,
    IReadOnlyList<CreatePersonLinkRequest>? Links = null);

/// <summary>
/// The new details of a person (<c>PUT /api/people/{id}</c>). All values replace the old ones: a value that is not sent
/// becomes empty. The contacts, the gift ideas, the loans, and the links do not change.
/// </summary>
public sealed record UpdatePersonRequest(
    string? Name,
    string? Nickname = null,
    string? Relationship = null,
    IReadOnlyList<string>? Groups = null,
    BirthdayDto? Birthday = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    int? ContactEveryDays = null);

/// <summary>A new link (<c>POST /api/people/{id}/links</c>).</summary>
/// <param name="Kind"><c>Documents.Document</c>, <c>Money.Transaction</c>, <c>Tasks.Task</c>, or <c>Home.Item</c>.</param>
/// <param name="TargetId">The id of the thing.</param>
public sealed record CreatePersonLinkRequest(string? Kind, string? TargetId);

/// <summary>A contact to log, or the new details of one (all values replace the old ones).</summary>
/// <param name="OccurredOn">The day of the contact. Without it: today.</param>
/// <param name="Kind">Free text, for example "Call".</param>
/// <param name="Note">What happened.</param>
public sealed record InteractionRequest(DateOnly? OccurredOn = null, string? Kind = null, string? Note = null);

/// <summary>A gift idea, or the new details of one (all values replace the old ones).</summary>
/// <param name="Title">What the gift is (required).</param>
/// <param name="Notes">Free text.</param>
/// <param name="Price">The price (0 or more, rounded to cents).</param>
/// <param name="Occasion">Free text, for example "Birthday".</param>
/// <param name="GivenOn">The day the gift was given; null for an idea.</param>
public sealed record GiftIdeaRequest(string? Title, string? Notes = null, decimal? Price = null, string? Occasion = null, DateOnly? GivenOn = null);

/// <summary>A loan, or the new details of one (all values replace the old ones).</summary>
/// <param name="ItemName">The thing (required).</param>
/// <param name="Direction"><c>Lent</c> (default) or <c>Borrowed</c>.</param>
/// <param name="LentOn">The day of the loan. Without it: today.</param>
/// <param name="DueOn">The day the thing should come back.</param>
/// <param name="ReturnedOn">The day the thing came back; null while the loan is open.</param>
/// <param name="Notes">Free text.</param>
public sealed record LoanRequest(
    string? ItemName,
    LoanDirection? Direction = null,
    DateOnly? LentOn = null,
    DateOnly? DueOn = null,
    DateOnly? ReturnedOn = null,
    string? Notes = null);

/// <summary>A group and how many people it has.</summary>
public sealed record GroupCountDto(string Name, int Count);

/// <summary>A birthday in the next days.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="Name">The name.</param>
/// <param name="Date">The day of the birthday.</param>
/// <param name="Days">How many days from today (0 = today).</param>
/// <param name="Age">The new age, when the year of birth is known.</param>
/// <param name="GiftIdeas">How many open gift ideas the person has.</param>
public sealed record UpcomingBirthdayDto(Guid PersonId, string Name, DateOnly Date, int Days, int? Age, int GiftIdeas);

/// <summary>The numbers of the People screen (<c>GET /api/people/summary</c>).</summary>
/// <param name="Date">Today, in the time zone of the module (<c>People:TimeZone</c>).</param>
/// <param name="PeopleCount">How many people.</param>
/// <param name="ReachOut">The people with an overdue reminder, most overdue first.</param>
/// <param name="Upcoming">The birthdays today and in the next 30 days (<c>People:UpcomingDays</c>), soonest first.</param>
/// <param name="OpenLoans">How many loans are open (lent and borrowed).</param>
/// <param name="OverdueLoans">How many open loans are after their due day.</param>
public sealed record PeopleSummaryDto(
    DateOnly Date,
    int PeopleCount,
    IReadOnlyList<PersonSummaryDto> ReachOut,
    IReadOnlyList<UpcomingBirthdayDto> Upcoming,
    int OpenLoans,
    int OverdueLoans);

/// <summary>The kinds of link that the People module knows.</summary>
public static class PersonLinkKinds
{
    /// <summary>A document of the Documents module. The target id is the document id.</summary>
    public const string DocumentsDocument = "Documents.Document";

    /// <summary>A finance transaction (for example a gift). The target id is the transaction id (a GUID).</summary>
    public const string MoneyTransaction = "Money.Transaction";

    /// <summary>A task of the Tasks module. The target id is the task id.</summary>
    public const string TasksTask = "Tasks.Task";

    /// <summary>An item of the Home module (for example a lent thing). The target id is the item id.</summary>
    public const string HomeItem = "Home.Item";

    public static readonly IReadOnlyList<string> All = [DocumentsDocument, MoneyTransaction, TasksTask, HomeItem];
}

/// <summary>
/// People to import (<c>POST /api/people/import</c>, T105): the text of a vCard file (<c>.vcf</c>, from a phone or Google
/// Contacts), or a list of people (the Android app reads the phone contacts). Send one of the two.
/// </summary>
/// <param name="VCard">The text of a vCard file (one or more contacts).</param>
/// <param name="People">People to import, as for <c>POST /api/people</c> (the links are left out).</param>
/// <param name="DryRun">True (the default): only say what would happen, so the client can show a preview.</param>
/// <param name="Only">The indexes (from 0, in the file or list order) to import; null: all. The others are skipped.</param>
public sealed record PeopleImportRequest(
    string? VCard = null,
    IReadOnlyList<CreatePersonRequest>? People = null,
    bool DryRun = true,
    IReadOnlyList<int>? Only = null);

/// <summary>What an import does with one contact.</summary>
public enum PeopleImportAction
{
    /// <summary>A new person.</summary>
    Add,

    /// <summary>The same person is there (same email, phone, or name): the import fills only the empty details and adds groups.</summary>
    Merge,

    /// <summary>The same person is there, and the contact has nothing new.</summary>
    Same,

    /// <summary>Not imported: not chosen (<c>only</c>), or a value cannot be used (<see cref="PeopleImportItemDto.Reason"/>).</summary>
    Skip
}

/// <summary>One contact of an import.</summary>
/// <param name="Index">The place in the file or list, from 0.</param>
/// <param name="Person">The contact, as read.</param>
/// <param name="ExistingId">The person that the contact matches (Merge, Same), or the new person after an import (Add).</param>
/// <param name="ExistingName">The name of the matching person.</param>
/// <param name="MatchedBy"><c>email</c>, <c>phone</c>, or <c>name</c>; null for no match.</param>
/// <param name="NewFields">The details that the import adds to the matching person (Merge), for example <c>birthday</c>.</param>
public sealed record PeopleImportItemDto(
    int Index,
    PeopleImportAction Action,
    CreatePersonRequest Person,
    Guid? ExistingId,
    string? ExistingName,
    string? MatchedBy,
    IReadOnlyList<string> NewFields,
    string? Reason);

/// <summary>The result of an import: the counts, and each contact in order.</summary>
public sealed record PeopleImportResultDto(
    bool DryRun,
    int Added,
    int Merged,
    int Unchanged,
    int Skipped,
    IReadOnlyList<PeopleImportItemDto> People);

/// <summary>The limits of the import.</summary>
public static class PeopleImportLimits
{
    /// <summary>The most contacts in one import.</summary>
    public const int MaxContacts = 2000;

    /// <summary>The biggest vCard text (characters). Photos make a file big; they are left out.</summary>
    public const int MaxVCardLength = 20_000_000;
}
