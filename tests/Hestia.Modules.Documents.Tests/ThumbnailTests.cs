using System.Net;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Modules;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// T61: <c>GET /api/documents/{id}/files/{fileId}/thumbnail</c> gives a small JPEG of a file: the first page of a PDF,
/// or the photo, the right way up. It is made at the first request and kept in <c>thumbs/</c>.
/// </summary>
public class ThumbnailTests
{
    [Fact]
    public async Task APhoto_GetsAScaledJpeg_ThatIsKept_AndCachedByTheClient()
    {
        await using var host = DocumentsHost.Start();
        var photo = new SampleFile("photo.jpg", Jpeg(1200, 800, SKColors.SteelBlue), FileTypes.Jpeg);
        var document = await host.UploadOkAsync("Sofa photo", "Other", files: photo);
        var url = ThumbnailUrl(document);

        var response = await host.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        using var thumbnail = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((480, 320), (thumbnail.Width, thumbnail.Height));

        // Kept on the volume, under the storage key of the file; the second request reads it.
        var kept = $"{DocumentStorage.ThumbnailFolder}/{photo.Sha256[..2]}/{photo.Sha256}.jpg";
        Assert.Contains(kept, host.StoredFiles());
        File.SetLastWriteTimeUtc(Path.Combine(host.StoragePath, kept), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(url)).StatusCode);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(host.StoragePath, kept)));
    }

    [Fact]
    public async Task APhotoOnItsSide_IsTurned_AsItsExifOrientationSays()
    {
        await using var host = DocumentsHost.Start();
        // Stored 300 × 200, red on the left and blue on the right, with orientation 6: "turn 90° clockwise to show".
        var photo = new SampleFile("phone.jpg", WithOrientation(HalvesJpeg(300, 200), 6), FileTypes.Jpeg);
        var document = await host.UploadOkAsync("Phone photo", "Other", files: photo);

        using var thumbnail = SKBitmap.Decode(await host.Client.GetByteArrayAsync(ThumbnailUrl(document)));

        // Upright: 200 × 300, the red half at the top.
        Assert.Equal((200, 300), (thumbnail.Width, thumbnail.Height));
        AssertNear(SKColors.Red, thumbnail.GetPixel(100, 40));
        AssertNear(SKColors.Blue, thumbnail.GetPixel(100, 260));
    }

    [Fact]
    public async Task ASmallPng_KeepsItsSize_AndPngAndWebp_Work()
    {
        await using var host = DocumentsHost.Start();
        var document = await host.UploadOkAsync("Receipts", "Receipt", files: [SampleFile.Png, SampleFile.Webp]);

        foreach (var file in document.Files)
        {
            var response = await host.Client.GetAsync($"{DocumentsHost.Documents}/{document.Id}/files/{file.Id}/thumbnail");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var thumbnail = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
            Assert.True(Math.Max(thumbnail.Width, thumbnail.Height) <= DocumentThumbnails.Size, $"{thumbnail.Width} × {thumbnail.Height}");
        }
    }

    [Fact]
    public async Task ALargeTransparentPng_IsScaled_OnWhite()
    {
        await using var host = DocumentsHost.Start();
        using var bitmap = new SKBitmap(1000, 500);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var document = await host.UploadOkAsync("Plan", "Other", files: new SampleFile("plan.png", png.ToArray(), FileTypes.Png));

        var response = await host.Client.GetAsync(ThumbnailUrl(document));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var thumbnail = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((480, 240), (thumbnail.Width, thumbnail.Height));
        AssertNear(SKColors.White, thumbnail.GetPixel(240, 120));
    }

    [Fact]
    public async Task APdf_GetsItsFirstPage()
    {
        Assert.SkipUnless(Tools.Have("pdftoppm"), Tools.Missing);
        await using var host = DocumentsHost.Start();
        var document = await host.UploadOkAsync("Warranty", "Warranty", files: SampleFile.Pdf);

        var response = await host.Client.GetAsync(ThumbnailUrl(document));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var thumbnail = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(DocumentThumbnails.Size, Math.Max(thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public async Task ADamagedFile_OrAnUnknownFile_Is404()
    {
        await using var host = DocumentsHost.Start();
        // A JPEG start, then nothing that a decoder can read.
        var damaged = new SampleFile("damaged.jpg", [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[64]], FileTypes.Jpeg);
        var document = await host.UploadOkAsync("Damaged", "Other", files: damaged);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(ThumbnailUrl(document))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync($"{DocumentsHost.Documents}/{document.Id}/files/{Guid.NewGuid()}/thumbnail")).StatusCode);
        Assert.DoesNotContain(host.StoredFiles(), key => key.StartsWith(DocumentStorage.ThumbnailFolder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheThumbnail_GoesWithItsFile()
    {
        await using var host = DocumentsHost.Start();
        var document = await host.UploadOkAsync("Old photo", "Other", files: new SampleFile("p.jpg", Jpeg(64, 64, SKColors.Olive), FileTypes.Jpeg));
        await host.Client.GetAsync(ThumbnailUrl(document));
        Assert.Equal(2, host.StoredFiles().Count);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{DocumentsHost.Documents}/{document.Id}")).StatusCode);

        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task CleanFiles_RemovesTheThumbnailOfAnUnusedFile_AndKeepsTheOthers()
    {
        await using var host = DocumentsHost.Start();
        var document = await host.UploadOkAsync("Kept", "Other", files: new SampleFile("k.jpg", Jpeg(64, 64, SKColors.Teal), FileTypes.Jpeg));
        await host.Client.GetAsync(ThumbnailUrl(document));
        var orphan = $"{DocumentStorage.ThumbnailFolder}/ab/{new string('a', 64)}.jpg";
        var orphanPath = Path.Combine(host.StoragePath, orphan);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllBytes(orphanPath, [1, 2, 3]);
        foreach (var path in Directory.EnumerateFiles(host.StoragePath, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }

        var command = host.Factory.Services.GetServices<IModuleCommand>().Single(c => c.Name == "documents-clean-files");
        var output = new StringWriter();
        Assert.Equal(0, await command.RunAsync(host.Factory.Services, ["--delete"], TextReader.Null, output, CancellationToken.None));

        Assert.Contains($"deleted   {orphan}", output.ToString());
        Assert.DoesNotContain("skipped", output.ToString());
        Assert.Equal(2, host.StoredFiles().Count);
        Assert.DoesNotContain(orphan, host.StoredFiles());
    }

    private static string ThumbnailUrl(DocumentDto document) =>
        $"{DocumentsHost.Documents}/{document.Id}/files/{document.Files[0].Id}/thumbnail";

    private static byte[] Jpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return Encode(bitmap);
    }

    /// <summary>Red on the left half, blue on the right half.</summary>
    private static byte[] HalvesJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        return Encode(bitmap);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>The JPEG with an EXIF segment (APP1) that has only the orientation tag (0x0112).</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little endian, the first IFD at 8
            0x01, 0x00, // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00 // no next IFD
        ];
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    private static void AssertNear(SKColor expected, SKColor actual)
    {
        var near = Math.Abs(expected.Red - actual.Red) < 40 && Math.Abs(expected.Green - actual.Green) < 40 &&
                   Math.Abs(expected.Blue - actual.Blue) < 40;
        Assert.True(near, $"Expected about {expected}, got {actual}.");
    }
}
