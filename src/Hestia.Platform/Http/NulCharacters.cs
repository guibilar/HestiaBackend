using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Hestia.Platform.Http;

/// <summary>
/// PostgreSQL cannot keep the NUL character (U+0000) in text: a value with it fails the whole command, and the client
/// got 500. Text pasted from a PDF or another app can have it, and nobody can see it. The host removes it where requests
/// come in, before the module endpoints and the finance proxy read them: from the query string (<c>%00</c>) and from a
/// JSON body (<c>\u0000</c>, the only way JSON can have it). It is not a JSON converter: a custom string converter makes
/// the OpenAPI document lose the type of every string.
/// </summary>
public static partial class NulCharacters
{
    /// <summary>A JSON body larger than this is passed on as it is (no module takes text this long).</summary>
    public const int MaxJsonBodyBytes = 1024 * 1024;

    // \u0000 that is an escape, not the text "\u0000" (an escaped backslash, then u0000): an even number of
    // backslashes (0, 2, …) before it. The group keeps those backslashes.
    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNul();

    /// <summary>The JSON text without NUL escapes (the same instance when it has none).</summary>
    public static string RemoveFromJson(string json) =>
        json.Contains(@"\u0000", StringComparison.Ordinal) ? JsonNul().Replace(json, "$1") : json;

    /// <summary>Removes NUL characters from the query string and from JSON bodies of the requests.</summary>
    public static IApplicationBuilder UseNulFreeRequests(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var query = request.QueryString.Value;
            if (query is not null && query.Contains("%00", StringComparison.Ordinal))
            {
                request.QueryString = new QueryString(query.Replace("%00", string.Empty, StringComparison.Ordinal));
            }
            if (request.HasJsonContentType() && request.ContentLength is null or <= MaxJsonBodyBytes)
            {
                await CleanJsonBodyAsync(request, context.RequestAborted);
            }
            await next(context);
        });

    private static async Task CleanJsonBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering(MaxJsonBodyBytes);
        string json;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096, leaveOpen: true))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }
        var cleaned = RemoveFromJson(json);
        if (ReferenceEquals(cleaned, json))
        {
            request.Body.Position = 0;
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(cleaned);
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
    }
}
