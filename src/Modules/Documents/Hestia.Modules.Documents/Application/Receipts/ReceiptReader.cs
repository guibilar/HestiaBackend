using System.Globalization;
using System.Text.RegularExpressions;

namespace Hestia.Modules.Documents.Application.Receipts;

/// <summary>What the text of a receipt says (T100). Each value is null when the text does not say it clearly.</summary>
/// <param name="Merchant">The store, from the first lines.</param>
/// <param name="Date">The day of the purchase.</param>
/// <param name="Total">The amount paid (the largest "total" on the receipt, never a subtotal or the tax).</param>
/// <param name="WarrantyMonths">The length of a warranty that the text names ("2 year warranty"), in months.</param>
/// <param name="WarrantyUntil">The end day of a warranty that the text names ("warranty until 2028-05-01").</param>
public sealed record ReceiptFacts(string? Merchant, DateOnly? Date, decimal? Total, int? WarrantyMonths, DateOnly? WarrantyUntil);

/// <summary>
/// Reads the OCR text of a receipt with simple rules (T100): no AI and no network, so it is fast and private. The rules
/// know the common forms of North American receipts. A value that the rules do not find clearly stays null.
/// </summary>
public static partial class ReceiptReader
{
    /// <param name="text">The OCR text.</param>
    /// <param name="today">Today: a receipt is not from the future.</param>
    public static ReceiptFacts Read(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ReceiptFacts(null, null, null, null, null);
        }
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        var (months, until) = Warranty(text, today);
        return new ReceiptFacts(Merchant(lines), Date(lines, today), Total(lines), months, until);
    }

    // ---- Total ----

    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[ ,]\d{3})+|\d+)[.,](\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Money();

    [GeneratedRegex(@"\b(grand\s*total|total|amount\s*(due|paid)|balance\s*(due)?|montant|to\s*pay|you\s*paid|charged)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TotalWords();

    [GeneratedRegex(@"\b(sub\s*-?\s*total|subtotal|tax|gst|hst|pst|qst|tvq|tps|saving|savings|discount|items?|qty|tip|change|cash\s*back|points|rewards?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotTotalWords();

    /// <summary>The largest amount on a "total" line that is not a subtotal, a tax, a saving, or the change.</summary>
    public static decimal? Total(IReadOnlyList<string> lines)
    {
        decimal? best = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!TotalWords().IsMatch(line) || NotTotalWords().IsMatch(line))
            {
                continue;
            }
            // The amount is on the line, or alone on the next one (OCR often splits the columns).
            var amount = LastAmount(line) ?? (i + 1 < lines.Count && !TotalWords().IsMatch(lines[i + 1]) ? LastAmount(lines[i + 1]) : null);
            if (amount is { } value && value > 0 && (best is null || value > best))
            {
                best = value;
            }
        }
        return best;
    }

    private static decimal? LastAmount(string line)
    {
        var matches = Money().Matches(line);
        if (matches.Count == 0)
        {
            return null;
        }
        var match = matches[^1];
        var whole = match.Groups[1].Value.Replace(",", string.Empty).Replace(" ", string.Empty);
        return decimal.TryParse($"{whole}.{match.Groups[2].Value}", NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            ? amount : null;
    }

    // ---- Date ----

    [GeneratedRegex(@"\b(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearFirst();

    [GeneratedRegex(@"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4}|\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearLast();

    [GeneratedRegex(@"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthFirst();

    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?,?\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayFirst();

    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// The first date in the text that is not in the future and not before 2000. For 03/04/2026, the North American
    /// order (month first) wins, unless the first number cannot be a month.
    /// </summary>
    public static DateOnly? Date(IReadOnlyList<string> lines, DateOnly today)
    {
        foreach (var line in lines)
        {
            foreach (var date in DatesIn(line))
            {
                if (date.Year >= 2000 && date <= today.AddDays(1))
                {
                    return date;
                }
            }
        }
        return null;
    }

    private static IEnumerable<DateOnly> DatesIn(string line)
    {
        foreach (Match m in YearFirst().Matches(line))
        {
            if (Make(Int(m.Groups[1]), Int(m.Groups[2]), Int(m.Groups[3])) is { } date)
            {
                yield return date;
            }
        }
        foreach (Match m in MonthFirst().Matches(line))
        {
            if (Make(Int(m.Groups[3]), Month(m.Groups[1].Value), Int(m.Groups[2])) is { } date)
            {
                yield return date;
            }
        }
        foreach (Match m in DayFirst().Matches(line))
        {
            if (Make(Int(m.Groups[3]), Month(m.Groups[2].Value), Int(m.Groups[1])) is { } date)
            {
                yield return date;
            }
        }
        foreach (Match m in YearLast().Matches(line))
        {
            var a = Int(m.Groups[1]);
            var b = Int(m.Groups[2]);
            var year = Int(m.Groups[3]);
            if (year < 100)
            {
                year += 2000;
            }
            var found = (a <= 12 ? Make(year, a, b) : null) ?? Make(year, b, a);
            if (found is { } date)
            {
                yield return date;
            }
        }
    }

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static int Month(string name) => Array.IndexOf(MonthNames, name[..3].ToLowerInvariant()) + 1;

    private static DateOnly? Make(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day) : null;

    // ---- Merchant ----

    [GeneratedRegex(@"\b(receipt|invoice|welcome|thank|store\s*#|tel|phone|www\.|http|\.com|cashier|register|transaction|order\s*#|date|time)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotMerchant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>The store: the first of the first five lines that is mostly letters, not an address, a phone, or a heading.</summary>
    public static string? Merchant(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(5))
        {
            var letters = line.Count(char.IsLetter);
            var digits = line.Count(char.IsDigit);
            if (letters < 3 || letters < line.Length / 2 || digits > 3 || NotMerchant().IsMatch(line))
            {
                continue;
            }
            var name = line.Trim(' ', '*', '-', '=', '#', '.');
            if (name.Length > 60)
            {
                name = name[..60].Trim();
            }
            // "COSTCO WHOLESALE" reads better as "Costco Wholesale".
            return name.Any(char.IsLower) ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        }
        return null;
    }

    // ---- Warranty ----

    [GeneratedRegex(@"\b(\d{1,2}|one|two|three|four|five|ten)[\s-]*(years?|yrs?|months?|mos?)\b[^.\n]{0,30}?\bwarrant",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthBefore();

    [GeneratedRegex(@"\bwarrant(y|ies)\b[^.\n\d]{0,30}?\b(\d{1,2}|one|two|three|four|five|ten)[\s-]*(years?|yrs?|months?|mos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthAfter();

    [GeneratedRegex(@"\bwarrant[a-z]*\b[^\n]{0,40}?\b(until|expires?|ends?|valid\s*(to|until|through)|through)\b[^\n]{0,15}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarrantyEnd();

    private static (int? Months, DateOnly? Until) Warranty(string text, DateOnly today)
    {
        DateOnly? until = null;
        if (WarrantyEnd().Match(text) is { Success: true } end)
        {
            until = DatesIn(text.Substring(end.Index, Math.Min(text.Length - end.Index, end.Length + 20))).Cast<DateOnly?>().FirstOrDefault();
        }
        var match = LengthBefore().Match(text);
        string? number = null, unit = null;
        if (match.Success)
        {
            (number, unit) = (match.Groups[1].Value, match.Groups[2].Value);
        }
        else if (LengthAfter().Match(text) is { Success: true } after)
        {
            (number, unit) = (after.Groups[2].Value, after.Groups[3].Value);
        }
        int? months = null;
        if (number is not null && unit is not null)
        {
            var count = number.ToLowerInvariant() switch
            {
                "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "ten" => 10,
                _ => int.Parse(number, CultureInfo.InvariantCulture)
            };
            months = unit.StartsWith('y') || unit.StartsWith('Y') ? count * 12 : count;
            if (months is < 1 or > 360)
            {
                months = null;
            }
        }
        return (months, until > today.AddYears(-1) ? until : null);
    }
}
