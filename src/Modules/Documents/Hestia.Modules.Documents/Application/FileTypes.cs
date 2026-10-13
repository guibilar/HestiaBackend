using System.Text;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The file types that a document can have. The host finds the type in the first bytes of the content (the "magic
/// number"), and never trusts the type or the file name that the client sends.
/// </summary>
public static class FileTypes
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Heic = "image/heic";
    public const string Webp = "image/webp";

    public static readonly IReadOnlyList<string> Supported = [Pdf, Jpeg, Png, Heic, Webp];

    /// <summary>How many bytes <see cref="Detect"/> needs.</summary>
    public const int HeaderLength = 16;

    // The ISO base media "brands" of HEIF images (HEIC from phones, and the general HEIF brands).
    private static readonly HashSet<string> HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1"];

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The content type of a file from its first bytes, or null for a type that a document cannot have.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }
        if (header.StartsWith(JpegStart))
        {
            return Jpeg;
        }
        if (header.StartsWith(PngStart))
        {
            return Png;
        }
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return Webp;
        }
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8) &&
            HeifBrands.Contains(Encoding.ASCII.GetString(header.Slice(8, 4))))
        {
            return Heic;
        }
        return null;
    }

    /// <summary>A short name of the type for messages, for example "PDF".</summary>
    public static string Name(string contentType) => contentType switch
    {
        Pdf => "PDF",
        Jpeg => "JPEG",
        Png => "PNG",
        Heic => "HEIC",
        Webp => "WebP",
        _ => contentType
    };
}
