using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application.Ocr;

/// <summary>The text of one file.</summary>
/// <param name="Success">True when the OCR worked on the whole file.</param>
/// <param name="Text">The text that was found (also after a failure: for example the text layer of a PDF).</param>
/// <param name="Error">Why the OCR failed, or null.</param>
public sealed record OcrResult(bool Success, string Text, string? Error)
{
    public static OcrResult Succeeded(string text) => new(true, text, null);

    public static OcrResult Failed(string error, string text = "") => new(false, text, error);
}

/// <summary>Reads the text of one stored file (D3: rules and OCR, no AI). Tests replace it.</summary>
public interface IOcrEngine
{
    /// <param name="path">The full path of the file.</param>
    /// <param name="contentType">The type of the file (<see cref="FileTypes"/>).</param>
    /// <param name="cancellationToken">Stops the OCR (the host stops).</param>
    Task<OcrResult> ExtractTextAsync(string path, string contentType, CancellationToken cancellationToken);
}

/// <summary>
/// The OCR with the programs in the host image (D9):
/// <list type="bullet">
/// <item>PDF: <c>ocrmypdf --skip-text</c> adds a text layer to the pages that have none (Tesseract), then
/// <c>pdftotext -layout</c> reads all text, so a PDF with text keeps its own text. The layout mode keeps the words of a
/// line together: without it, the OCR layer of some ocrmypdf versions comes out one word per line, in column order.</item>
/// <item>JPEG, PNG, WebP: <c>tesseract</c>.</item>
/// <item>HEIC: <c>heif-convert</c> (or <c>heif-dec</c>) makes a PNG first, then <c>tesseract</c>.</item>
/// </list>
/// </summary>
public sealed partial class CommandLineOcrEngine(IOptions<DocumentsOptions> options, ILogger<CommandLineOcrEngine> logger) : IOcrEngine
{
    // ocrmypdf exit codes: https://ocrmypdf.readthedocs.io/en/latest/advanced.html#return-code-policy
    private const int OcrMyPdfEncrypted = 8;

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public async Task<OcrResult> ExtractTextAsync(string path, string contentType, CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("hestia-ocr-");
        try
        {
            return contentType switch
            {
                FileTypes.Pdf => await PdfAsync(path, work.FullName, cancellationToken),
                FileTypes.Heic => await HeicAsync(path, work.FullName, cancellationToken),
                FileTypes.Jpeg or FileTypes.Png or FileTypes.Webp => await ImageAsync(path, cancellationToken),
                _ => OcrResult.Failed($"There is no OCR for {contentType}.")
            };
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Documents: could not delete the OCR folder {Folder}", work.FullName);
            }
        }
    }

    private OcrOptions Settings => options.Value.Ocr;

    private async Task<OcrResult> PdfAsync(string path, string work, CancellationToken cancellationToken)
    {
        var output = Path.Combine(work, "ocr.pdf");
        var ocr = await RunAsync("ocrmypdf",
            ["--skip-text", "--output-type", "pdf", "--jobs", "1", "--quiet", "-l", Settings.Language, path, output],
            cancellationToken);
        var text = await RunAsync("pdftotext", ["-layout", "-enc", "UTF-8", ocr.Succeeded ? output : path, "-"], cancellationToken);
        if (ocr.Succeeded)
        {
            return text.Succeeded ? OcrResult.Succeeded(Clean(text.Output)) : OcrResult.Failed(Describe("pdftotext", text));
        }

        // The OCR failed. The text layer of the PDF itself (if it has one) is still worth keeping for the search.
        var error = ocr.ExitCode == OcrMyPdfEncrypted
            ? "The PDF is encrypted (it has a password), so it cannot get OCR."
            : Describe("ocrmypdf", ocr);
        return OcrResult.Failed(error, text.Succeeded ? Clean(text.Output) : string.Empty);
    }

    private async Task<OcrResult> ImageAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync("tesseract", [path, "stdout", "-l", Settings.Language], cancellationToken);
        return result.Succeeded ? OcrResult.Succeeded(Clean(result.Output)) : OcrResult.Failed(Describe("tesseract", result));
    }

    private async Task<OcrResult> HeicAsync(string path, string work, CancellationToken cancellationToken)
    {
        var png = Path.Combine(work, "image.png");
        // libheif 1.17 has heif-convert; newer versions call it heif-dec.
        var convert = await RunAsync("heif-convert", [path, png], cancellationToken);
        if (convert.NotFound)
        {
            convert = await RunAsync("heif-dec", [path, png], cancellationToken);
        }
        if (!convert.Succeeded || !File.Exists(png))
        {
            return OcrResult.Failed(Describe("heif-convert", convert));
        }
        return await ImageAsync(png, cancellationToken);
    }

    private Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(program, arguments, Settings.Timeout, cancellationToken);

    private string Describe(string program, ProcessResult result)
    {
        if (result.NotFound)
        {
            return $"{program} is not installed on the host.";
        }
        if (result.TimedOut)
        {
            return $"{program} did not finish in {Settings.Timeout}.";
        }
        var error = result.Error.Trim();
        if (error.Length > 500)
        {
            error = "…" + error[^500..];
        }
        return error.Length == 0 ? $"{program} failed (exit code {result.ExitCode})." : $"{program} failed (exit code {result.ExitCode}): {error}";
    }

    /// <summary>
    /// The text with Unix line ends, no page breaks, single spaces between words (the layout mode adds many), no spaces
    /// at line ends, and at most one empty line in a row.
    /// </summary>
    public static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n')
            .Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }
}

/// <summary>The result of one program run.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut = false, bool NotFound = false)
{
    public bool Succeeded => !TimedOut && !NotFound && ExitCode == 0;
}

/// <summary>Runs a program with arguments (no shell), with a timeout, and collects its output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        // Tesseract uses one thread per file, so OCR does not take every core of the home server.
        start.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new ProcessResult(-1, string.Empty, exception.Message, NotFound: true);
        }

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended at the same time.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, await output, await error, TimedOut: true);
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }
}
