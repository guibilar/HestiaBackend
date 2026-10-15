using Hestia.Modules.Documents.Application.Ocr;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Hestia.Modules.Documents.Application;

/// <summary>The thumbnail of a file could not be made (an unknown type, a damaged file, or a tool that is missing).</summary>
public sealed class ThumbnailUnavailableException(string message) : Exception(message);

/// <summary>
/// Small JPEG previews of the document files (T61), for the lists of the web and Android apps. The longest side is
/// <see cref="Size"/> pixels.
/// <list type="bullet">
/// <item>PDF: the first page, with <c>pdftoppm</c> (poppler-utils, in the API image).</item>
/// <item>JPEG, PNG, WebP: scaled with SkiaSharp, turned as the EXIF orientation says.</item>
/// <item>HEIC: <c>heif-convert</c> (or <c>heif-dec</c>) makes a PNG first, as for the OCR.</item>
/// </list>
/// A thumbnail is made at the first request and kept on the documents volume, in <c>thumbs/</c>, under the storage key of
/// its file (<see cref="DocumentStorage.ThumbnailPathOf"/>). The content of a key never changes, so a kept thumbnail is
/// always right. <c>documents-clean-files</c> removes the thumbnails of unused files.
/// </summary>
public sealed class DocumentThumbnails(DocumentStorage storage, ILogger<DocumentThumbnails> logger)
{
    /// <summary>The longest side of a thumbnail, in pixels: enough for a list row on a phone with a dense screen.</summary>
    public const int Size = 480;

    public const string ContentType = "image/jpeg";

    private const int Quality = 80;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The path of the thumbnail of the stored file <paramref name="key"/>; made now when it is not there yet.</summary>
    /// <exception cref="ThumbnailUnavailableException">The thumbnail cannot be made.</exception>
    public async Task<string> GetAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        var path = storage.ThumbnailPathOf(key);
        if (File.Exists(path))
        {
            return path;
        }

        var source = storage.PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var work = Path.Combine(storage.Root, DocumentStorage.TempFolder, $"thumb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var made = contentType switch
            {
                FileTypes.Pdf => await FromPdfAsync(source, work, cancellationToken),
                FileTypes.Heic => FromImage(await HeicToPngAsync(source, work, cancellationToken), work),
                FileTypes.Jpeg or FileTypes.Png or FileTypes.Webp => FromImage(source, work),
                _ => throw new ThumbnailUnavailableException($"No thumbnail for the type {contentType}.")
            };
            // Two requests at the same time make the same thumbnail: the last move wins, and both are right.
            File.Move(made, path, overwrite: true);
            return path;
        }
        catch (ThumbnailUnavailableException exception)
        {
            logger.LogWarning("Documents: no thumbnail for the file {StorageKey}: {Reason}", key, exception.Message);
            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // documents-clean-files removes what stays in tmp/.
            }
        }
    }

    private static async Task<string> FromPdfAsync(string source, string work, CancellationToken cancellationToken)
    {
        var output = Path.Combine(work, "page");
        var result = await ProcessRunner.RunAsync("pdftoppm",
            ["-jpeg", "-jpegopt", $"quality={Quality}", "-f", "1", "-l", "1", "-scale-to", Size.ToString(), "-singlefile", source, output],
            ToolTimeout, cancellationToken);
        var jpeg = output + ".jpg";
        if (result.NotFound || result.TimedOut || result.ExitCode != 0 || !File.Exists(jpeg))
        {
            throw new ThumbnailUnavailableException(Describe("pdftoppm", result));
        }
        return jpeg;
    }

    private static async Task<string> HeicToPngAsync(string source, string work, CancellationToken cancellationToken)
    {
        var png = Path.Combine(work, "photo.png");
        // libheif 1.17 has heif-convert; newer versions call it heif-dec.
        var result = await ProcessRunner.RunAsync("heif-convert", [source, png], ToolTimeout, cancellationToken);
        if (result.NotFound)
        {
            result = await ProcessRunner.RunAsync("heif-dec", [source, png], ToolTimeout, cancellationToken);
        }
        if (result.NotFound || result.TimedOut || result.ExitCode != 0 || !File.Exists(png))
        {
            throw new ThumbnailUnavailableException(Describe("heif-convert", result));
        }
        return png;
    }

    /// <summary>A photo scaled to <see cref="Size"/>, the right way up, as a JPEG file in <paramref name="work"/>.</summary>
    internal static string FromImage(string source, string work)
    {
        using var codec = SKCodec.Create(source) ?? throw new ThumbnailUnavailableException("The image cannot be read.");
        var info = codec.Info;
        // Decode at a smaller size when the codec can (JPEG: 1/8 to 8/8), which saves memory for a large photo. Take
        // the smallest size that is still at least the thumbnail size; the scale below makes the exact size.
        var decodeSize = info.Size;
        for (var eighths = 1; eighths < 8; eighths++)
        {
            var candidate = codec.GetScaledDimensions(eighths / 8f);
            if (Math.Max(candidate.Width, candidate.Height) >= Math.Min(Size, Math.Max(info.Width, info.Height)))
            {
                decodeSize = candidate;
                break;
            }
        }
        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var status = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ThumbnailUnavailableException($"The image cannot be read ({status}).");
        }

        var fit = Math.Min(1f, (float)Size / Math.Max(decoded.Width, decoded.Height));
        var width = Math.Max(1, (int)Math.Round(decoded.Width * fit));
        var height = Math.Max(1, (int)Math.Round(decoded.Height * fit));
        using var scaled = decoded.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new ThumbnailUnavailableException("The image cannot be scaled.");
        using var upright = Orient(scaled, codec.EncodedOrigin);

        var output = Path.Combine(work, "photo.jpg");
        using (var stream = File.Create(output))
        {
            // JPEG has no transparency: a transparent PNG gets a white background.
            using var surface = SKSurface.Create(new SKImageInfo(upright.Width, upright.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawBitmap(upright, 0, 0, SKSamplingOptions.Default);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
            data.SaveTo(stream);
        }
        return output;
    }

    /// <summary>The bitmap turned as the EXIF orientation says (a phone photo is often stored on its side).</summary>
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap.Copy();
        }
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(result);
        var w = bitmap.Width;
        var h = bitmap.Height;
        // Each case maps the stored pixel (x, y) to its place in the upright image. The last canvas call acts first.
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.Scale(1, -1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
        return result;
    }

    private static string Describe(string program, ProcessResult result) =>
        result.NotFound ? $"{program} is not installed."
        : result.TimedOut ? $"{program} took longer than {ToolTimeout.TotalSeconds} seconds."
        : $"{program} failed (exit code {result.ExitCode}): {result.Error.Trim()}";
}
