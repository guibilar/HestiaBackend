using System.Text.RegularExpressions;

namespace Hestia.Modules.Documents.Application;

/// <summary>The settings of the Documents module (section <c>Documents</c>).</summary>
public sealed partial class DocumentsOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// The folder of the files (D9). In Docker, the volume <c>hestia_documents</c> at <c>/data/documents</c>. A relative
    /// path is below the content root of the host. Required.
    /// </summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>The largest file that an upload can have, in MB.</summary>
    public int MaxFileSizeMb { get; set; } = 25;

    /// <summary>The largest upload (all files and the details together), in MB.</summary>
    public int MaxUploadSizeMb { get; set; } = 100;

    /// <summary>The most files that one document can have.</summary>
    public int MaxFilesPerDocument { get; set; } = 10;

    /// <summary>
    /// The file types that an upload can have, from <see cref="FileTypes.Supported"/>. Empty: all of them. The host finds
    /// the type in the file content; the type that the client sends does not count.
    /// </summary>
    public string[]? AllowedContentTypes { get; set; }

    /// <summary>
    /// IANA time zone of this module, for "today" in <c>expiring=soon</c> (T86). Without a value, the shared
    /// Hestia:TimeZone (T85).
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public OcrOptions Ocr { get; set; } = new();

    public ExpiryOptions Expiry { get; set; } = new();

    public FileCheckOptions FileCheck { get; set; } = new();

    public long MaxFileSize => MaxFileSizeMb * 1024L * 1024L;

    public long MaxUploadSize => MaxUploadSizeMb * 1024L * 1024L;

    public IReadOnlyList<string> ContentTypes =>
        AllowedContentTypes is { Length: > 0 } types ? types : FileTypes.Supported;

    [GeneratedRegex(@"^[A-Za-z0-9_]+(\+[A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Languages();

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(StoragePath) &&
        MaxFileSizeMb > 0 && MaxUploadSizeMb >= MaxFileSizeMb && MaxFilesPerDocument > 0 &&
        ContentTypes.All(type => FileTypes.Supported.Contains(type)) &&
        Languages().IsMatch(Ocr.Language) && Ocr.Timeout > TimeSpan.Zero && Ocr.PollInterval > TimeSpan.Zero &&
        Expiry.SoonDays >= 0 && Expiry.ExpiredDays >= 0 &&
        FileCheck.Interval > TimeSpan.Zero && FileCheck.Interval <= FileCheckOptions.MaxDelay &&
        FileCheck.FirstDelay >= TimeSpan.Zero && FileCheck.FirstDelay <= FileCheckOptions.MaxDelay;

    public const string ValidationMessage =
        "Documents: set Documents:StoragePath to the folder of the files (in Docker, the documents volume). " +
        "MaxFileSizeMb and MaxFilesPerDocument must be more than zero, and MaxUploadSizeMb at least MaxFileSizeMb. " +
        "AllowedContentTypes can only have application/pdf, image/jpeg, image/png, image/heic, and image/webp. " +
        "Ocr:Language is a Tesseract language such as eng or eng+fra; Ocr:Timeout and Ocr:PollInterval are more than zero. " +
        "FileCheck:Interval is more than zero, FileCheck:FirstDelay is zero or more, and both are at most 49 days.";
}

/// <summary>The text recognition (section <c>Documents:Ocr</c>).</summary>
public sealed class OcrOptions
{
    /// <summary>False: new documents get no OCR (status NotNeeded). The host needs ocrmypdf and Tesseract for OCR.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Tesseract language(s), for example <c>eng</c> or <c>eng+fra</c>. Each needs its language package.</summary>
    public string Language { get; set; } = "eng";

    /// <summary>How long the OCR of one file can take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the OCR worker looks for documents to read. An upload wakes it at once.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>The expiry alerts, on Today and in <c>GET /api/alerts</c> (section <c>Documents:Expiry</c>).</summary>
public sealed class ExpiryOptions
{
    /// <summary>A document that expires in this many days, or fewer, has an alert.</summary>
    public int SoonDays { get; set; } = 30;

    /// <summary>A document that expired this many days ago, or fewer, still has an alert.</summary>
    public int ExpiredDays { get; set; } = 7;
}

/// <summary>The scheduled check of the documents volume, <see cref="FileCheckWorker"/> (section <c>Documents:FileCheck</c>, T64).</summary>
public sealed class FileCheckOptions
{
    /// <summary>The longest delay that a timer can have (about 49.7 days).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(49);

    /// <summary>False: the host does not check the volume on a schedule. The command still works.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The time between two checks.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The time from the start of the host to the first check (so the check does not slow the start).</summary>
    public TimeSpan FirstDelay { get; set; } = TimeSpan.FromMinutes(10);
}
