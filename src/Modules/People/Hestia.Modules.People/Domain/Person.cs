using Hestia.Modules.People.Contracts;
using Hestia.Platform.Domain;

namespace Hestia.Modules.People.Domain;

/// <summary>A person the user cares about.</summary>
public class Person : BaseEntity
{
    public const int MaxNameLength = 200;
    public const int MaxNicknameLength = 100;
    public const int MaxRelationshipLength = 50;
    public const int MaxEmailLength = 254;
    public const int MaxPhoneLength = 50;
    public const int MaxNotesLength = 4000;
    public const int MaxContactEveryDays = 3650;

    public string Name { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public string? Relationship { get; set; }

    /// <summary>The groups, stored as <c>|group1|group2|</c> (see <see cref="PersonGroups"/>), or empty.</summary>
    public string Groups { get; set; } = string.Empty;

    /// <summary>The month of the birthday (1 to 12), or null. Set together with <see cref="BirthDay"/>.</summary>
    public int? BirthMonth { get; set; }

    public int? BirthDay { get; set; }

    /// <summary>The year of birth, or null when the user does not know it.</summary>
    public int? BirthYear { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    /// <summary>The reach-out reminder in days, or null for no reminder.</summary>
    public int? ContactEveryDays { get; set; }

    /// <summary>The day of the latest contact, or null. The services keep it equal to the latest <see cref="Interaction"/>.</summary>
    public DateOnly? LastContactOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<PersonLink> Links { get; set; } = [];
}

/// <summary>A contact with a person: a call, a coffee, a visit.</summary>
public class Interaction : BaseEntity
{
    public const int MaxKindLength = 50;
    public const int MaxNoteLength = 4000;

    public Guid PersonId { get; set; }
    public DateOnly OccurredOn { get; set; }
    public string? Kind { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A gift idea for a person. With <see cref="GivenOn"/>, the gift was given.</summary>
public class GiftIdea : BaseEntity
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 2000;
    public const int MaxOccasionLength = 50;

    public Guid PersonId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal? Price { get; set; }
    public string? Occasion { get; set; }
    public DateOnly? GivenOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A thing lent to a person, or borrowed from a person.</summary>
public class Loan : BaseEntity
{
    public const int MaxItemNameLength = 200;
    public const int MaxNotesLength = 2000;

    public Guid PersonId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public LoanDirection Direction { get; set; }
    public DateOnly LentOn { get; set; }
    public DateOnly? DueOn { get; set; }

    /// <summary>The day the thing came back, or null while the loan is open.</summary>
    public DateOnly? ReturnedOn { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A link from a person to something outside the module. The kind and the id are text, as in the other modules.</summary>
public class PersonLink : BaseEntity
{
    public const int MaxKindLength = 50;
    public const int MaxTargetIdLength = 100;

    public Guid PersonId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
