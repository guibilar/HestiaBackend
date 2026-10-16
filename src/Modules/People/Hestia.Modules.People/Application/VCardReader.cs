using System.Globalization;
using System.Text;
using Hestia.Modules.People.Contracts;

namespace Hestia.Modules.People.Application;

/// <summary>
/// Reads the contacts of a vCard file (T105): vCard 2.1 (Android exports, with QUOTED-PRINTABLE), 3.0 (iPhone, Google
/// Contacts), and 4.0. It reads the name, the nickname, the first email and phone (the preferred or mobile one first), the
/// birthday, the note, and the categories (as groups). The other emails and phones go into the notes. Photos are left out.
/// </summary>
public static class VCardReader
{
    /// <summary>A contact of the file, as a new person, and the line where it starts (for the messages).</summary>
    public sealed record Contact(int Line, CreatePersonRequest Person);

    private sealed record Property(string Name, Dictionary<string, List<string>> Parameters, string Value);

    public static IReadOnlyList<Contact> Read(string text)
    {
        var contacts = new List<Contact>();
        List<Property>? card = null;
        var cardLine = 0;
        var lineNumber = 0;
        foreach (var line in Unfold(text))
        {
            lineNumber++;
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                card = [];
                cardLine = lineNumber;
                continue;
            }
            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (card is not null)
                {
                    contacts.Add(new Contact(cardLine, ToPerson(card)));
                }
                card = null;
                continue;
            }
            if (card is not null && Parse(line) is { } property)
            {
                card.Add(property);
            }
        }
        return contacts;
    }

    /// <summary>
    /// The logical lines: a line that starts with a space or a tab continues the line before (RFC 6350); a
    /// QUOTED-PRINTABLE value that ends with "=" continues on the next line (vCard 2.1).
    /// </summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new StringBuilder();
        var softBreak = false;
        foreach (var raw in lines)
        {
            if (current.Length > 0 && (softBreak || (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))))
            {
                current.Append(softBreak ? raw : raw[1..]);
            }
            else
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                }
                current.Clear().Append(raw);
            }
            var value = current.ToString();
            softBreak = value.EndsWith('=') && value.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            if (softBreak)
            {
                current.Length--;
            }
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static Property? Parse(string line)
    {
        var colon = IndexOutsideQuotes(line, ':');
        if (colon <= 0)
        {
            return null;
        }
        var head = line[..colon].Split(';');
        // A group prefix ("item1.EMAIL") is left out.
        var name = head[0].Contains('.') ? head[0][(head[0].LastIndexOf('.') + 1)..] : head[0];
        var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in head.Skip(1))
        {
            var equals = part.IndexOf('=');
            // vCard 2.1 writes a type alone: "TEL;CELL:…".
            var key = equals < 0 ? "TYPE" : part[..equals];
            var values = (equals < 0 ? part : part[(equals + 1)..]).Trim('"').Split(',');
            if (!parameters.TryGetValue(key, out var list))
            {
                parameters[key] = list = [];
            }
            list.AddRange(values.Select(v => v.Trim()));
        }
        var value = line[(colon + 1)..];
        if (Has(parameters, "ENCODING", "QUOTED-PRINTABLE") || Has(parameters, "TYPE", "QUOTED-PRINTABLE"))
        {
            value = QuotedPrintable(value, parameters.TryGetValue("CHARSET", out var charsets) ? charsets[0] : "UTF-8");
        }
        return new Property(name.ToUpperInvariant(), parameters, value);
    }

    private static int IndexOutsideQuotes(string line, char character)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == character && !quoted)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool Has(Dictionary<string, List<string>> parameters, string key, string value) =>
        parameters.TryGetValue(key, out var values) && values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string QuotedPrintable(string value, string charset)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length &&
                byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(bytes.ToArray());
    }

    /// <summary>A text value: the escapes <c>\n</c>, <c>\,</c>, <c>\;</c>, and <c>\\</c> become their characters.</summary>
    private static string Text(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString().Trim();
    }

    /// <summary>The parts of a structured value (N, ADR), split at ";" that is not escaped.</summary>
    private static List<string> Parts(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[++i]);
            }
            else if (value[i] == separator)
            {
                parts.Add(Text(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }
        parts.Add(Text(current.ToString()));
        return parts;
    }

    private static CreatePersonRequest ToPerson(List<Property> card)
    {
        string? Value(string name) => card.FirstOrDefault(p => p.Name == name && p.Value.Trim().Length > 0) is { } p ? Text(p.Value) : null;

        var name = Value("FN");
        if (string.IsNullOrWhiteSpace(name) && card.FirstOrDefault(p => p.Name == "N") is { } n)
        {
            // N: family; given; additional; prefix; suffix.
            var parts = Parts(n.Value, ';');
            name = string.Join(' ', new[] { Get(parts, 3), Get(parts, 1), Get(parts, 2), Get(parts, 0), Get(parts, 4) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Value("ORG") is { } org ? Parts(org, ';')[0] : null;
        }

        var emails = Ordered(card, "EMAIL").Select(p => Text(p.Value)).Where(v => v.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var phones = Ordered(card, "TEL").Select(p => Text(p.Value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? p.Value[4..] : p.Value))
            .Where(v => v.Length > 0).Distinct().ToList();
        var notes = new List<string>();
        if (Value("NOTE") is { } note)
        {
            notes.Add(note);
        }
        if (Value("ORG") is { } organization && Parts(organization, ';')[0] is { Length: > 0 } company && company != name)
        {
            notes.Add($"Works at {company}" + (Value("TITLE") is { } title ? $" ({title})" : string.Empty));
        }
        if (emails.Count > 1)
        {
            notes.Add("Other emails: " + string.Join(", ", emails.Skip(1)));
        }
        if (phones.Count > 1)
        {
            notes.Add("Other phones: " + string.Join(", ", phones.Skip(1)));
        }
        var groups = card.Where(p => p.Name == "CATEGORIES")
            .SelectMany(p => Parts(p.Value, ','))
            // Google Contacts adds its own system group.
            .Where(g => g.Length > 0 && !g.Equals("myContacts", StringComparison.OrdinalIgnoreCase) && !g.Equals("starred", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new CreatePersonRequest(
            name?.Trim() ?? string.Empty,
            Nickname: Value("NICKNAME") is { } nickname ? Parts(nickname, ',')[0] : null,
            Groups: groups.Count == 0 ? null : groups,
            Birthday: Birthday(Value("BDAY")),
            Email: emails.FirstOrDefault(),
            Phone: phones.FirstOrDefault(),
            Notes: notes.Count == 0 ? null : string.Join("\n", notes));
    }

    private static string? Get(List<string> parts, int index) => index < parts.Count ? parts[index] : null;

    /// <summary>The values of a property, the preferred first, then the mobile ones, then the file order.</summary>
    private static IEnumerable<Property> Ordered(List<Property> card, string name) =>
        card.Where(p => p.Name == name && p.Value.Trim().Length > 0)
            .Select((p, index) => (p, index))
            .OrderBy(x => Has(x.p.Parameters, "TYPE", "PREF") || x.p.Parameters.ContainsKey("PREF") ? 0 : 1)
            .ThenBy(x => Has(x.p.Parameters, "TYPE", "CELL") ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.p);

    /// <summary>A birthday: 1990-10-20, 19901020, --1020, --10-20, or 1990-10-20T00:00:00Z. Null when it does not read.</summary>
    public static BirthdayDto? Birthday(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var t = text.IndexOf('T');
        if (t > 0)
        {
            text = text[..t];
        }
        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            var digits = text[2..].Replace("-", string.Empty);
            return digits.Length == 4 && int.TryParse(digits[..2], out var m) && int.TryParse(digits[2..], out var d) && Valid(m, d, null)
                ? new BirthdayDto(m, d) : null;
        }
        var plain = text.Replace("-", string.Empty);
        if (plain.Length == 8 && int.TryParse(plain[..4], out var year) && int.TryParse(plain[4..6], out var month) &&
            int.TryParse(plain[6..], out var day) && Valid(month, day, year))
        {
            // Some phones write 1604 for "no year".
            return year < 1900 ? new BirthdayDto(month, day) : new BirthdayDto(month, day, year);
        }
        return null;
    }

    private static bool Valid(int month, int day, int? year) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year is >= 1 and <= 9999 ? year.Value : 2000, month);
}
