using System.Text.RegularExpressions;
using Hestia.Modules.People.Contracts;
using Hestia.Platform.Exceptions;

namespace Hestia.Modules.People.Domain;

/// <summary>
/// The groups of a person, as the tags of the other modules: one text column <c>|group1|group2|</c>, so a filter by group
/// is a text search for <c>|group|</c>. Groups are in lower case; the clients show them with a capital first letter.
/// </summary>
public static partial class PersonGroups
{
    public const int MaxGroups = 20;
    public const int MaxGroupLength = 50;
    public const char Separator = '|';

    /// <summary>The longest stored value: 20 groups of 50 characters, each after a separator, and the last separator.</summary>
    public const int MaxStoredLength = MaxGroups * (MaxGroupLength + 1) + 1;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>One line of text: trimmed, with single spaces.</summary>
    public static string OneLine(string value) => Spaces().Replace(value.Trim(), " ");

    /// <summary>
    /// The groups as the user gave them, cleaned: each value can hold several groups with commas between them. A group is
    /// trimmed, in lower case, with single spaces. Empty groups and repeats are left out.
    /// </summary>
    /// <exception cref="InvalidInputException">A group is too long or has a "|", or there are too many groups.</exception>
    public static List<string> Normalize(IEnumerable<string?>? values)
    {
        var groups = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var part in (value ?? string.Empty).Split(','))
            {
                var group = OneLine(part).ToLowerInvariant();
                if (group.Length == 0 || groups.Contains(group))
                {
                    continue;
                }
                if (group.Contains(Separator))
                {
                    throw new InvalidInputException("groups", $"A group cannot have the character \"{Separator}\".");
                }
                if (group.Length > MaxGroupLength)
                {
                    throw new InvalidInputException("groups", $"A group can have at most {MaxGroupLength} characters.");
                }
                groups.Add(group);
            }
        }
        if (groups.Count > MaxGroups)
        {
            throw new InvalidInputException("groups", $"A person can be in at most {MaxGroups} groups.");
        }
        return groups;
    }

    public static string Store(IReadOnlyCollection<string> groups) =>
        groups.Count == 0 ? string.Empty : $"{Separator}{string.Join(Separator, groups)}{Separator}";

    public static List<string> Read(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The text to search for in the stored value, to find one group.</summary>
    public static string Pattern(string group) => $"{Separator}{group}{Separator}";
}

/// <summary>The checks and the clean-up of the values that the user sends.</summary>
public static class PeopleText
{
    /// <summary>The largest amount that an amount column can keep (numeric(12,2)).</summary>
    public const decimal MaxAmount = 9_999_999_999.99m;

    public const int AmountPrecision = 12;
    public const int AmountScale = 2;

    /// <summary>One line of text (trimmed, single spaces), or null for an empty value.</summary>
    /// <exception cref="InvalidInputException">The text is too long.</exception>
    public static string? Line(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = PersonGroups.OneLine(value);
        if (text.Length > maxLength)
        {
            throw new InvalidInputException(field, $"The {field} can have at most {maxLength} characters.");
        }
        return text;
    }

    /// <summary>A required line of text.</summary>
    /// <exception cref="InvalidInputException">The text is empty or too long.</exception>
    public static string Required(string? value, string field, int maxLength, string emptyMessage) =>
        Line(value, field, maxLength) ?? throw new InvalidInputException(field, emptyMessage);

    /// <summary>Text with lines (trimmed), or null for an empty value.</summary>
    /// <exception cref="InvalidInputException">The text is too long.</exception>
    public static string? Block(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = value.Trim();
        if (text.Length > maxLength)
        {
            throw new InvalidInputException(field, $"The {field} can have at most {maxLength} characters.");
        }
        return text;
    }

    /// <summary>An amount, rounded to cents (half away from zero).</summary>
    /// <exception cref="InvalidInputException">The amount is less than 0 or too large.</exception>
    public static decimal? Amount(decimal? value, string field)
    {
        if (value is not { } amount)
        {
            return null;
        }
        if (amount < 0 || amount > MaxAmount)
        {
            throw new InvalidInputException(field, $"The amount must be from 0 to {MaxAmount:0.00}.");
        }
        return Math.Round(amount, AmountScale, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Birthdays: the check of a month and a day, the next birthday, and the age.</summary>
public static class Birthdays
{
    public const int MinYear = 1900;

    /// <summary>The checked birthday, or null.</summary>
    /// <exception cref="InvalidInputException">A month or a day that does not exist, or a year in the future.</exception>
    public static BirthdayDto? Check(BirthdayDto? birthday, DateOnly today)
    {
        if (birthday is null)
        {
            return null;
        }
        if (birthday.Month is < 1 or > 12)
        {
            throw new InvalidInputException("birthday", "The month must be from 1 to 12.");
        }
        // A leap year (2000) allows February 29.
        if (birthday.Day < 1 || birthday.Day > DateTime.DaysInMonth(2000, birthday.Month))
        {
            throw new InvalidInputException("birthday", "The day does not exist in this month.");
        }
        if (birthday.Year is { } year)
        {
            if (year < MinYear || year > today.Year)
            {
                throw new InvalidInputException("birthday", $"The year must be from {MinYear} to {today.Year}.");
            }
            if (!DateTime.IsLeapYear(year) && birthday.Month == 2 && birthday.Day == 29)
            {
                throw new InvalidInputException("birthday", $"{year} has no February 29.");
            }
            if (new DateOnly(year, birthday.Month, birthday.Day) > today)
            {
                throw new InvalidInputException("birthday", "The birthday cannot be in the future.");
            }
        }
        return birthday;
    }

    /// <summary>The birthday in <paramref name="year"/>. February 29 is February 28 in a year without it.</summary>
    public static DateOnly InYear(int month, int day, int year) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>The next birthday: today or later.</summary>
    public static DateOnly Next(int month, int day, DateOnly today)
    {
        var date = InYear(month, day, today.Year);
        return date >= today ? date : InYear(month, day, today.Year + 1);
    }

    public static DateOnly? Next(Person person, DateOnly today) =>
        person is { BirthMonth: { } month, BirthDay: { } day } ? Next(month, day, today) : null;

    /// <summary>The age on <paramref name="date"/> (a birthday), when the year of birth is known.</summary>
    public static int? AgeOn(Person person, DateOnly date) => person.BirthYear is { } year ? date.Year - year : null;
}
