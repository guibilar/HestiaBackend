using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Domain;
using Hestia.Platform.Exceptions;

namespace Hestia.Modules.Documents.Tests;

/// <summary>The small rules: file types from the content, tags, and file names.</summary>
public class RuleTests
{
    [Theory]
    [InlineData("scanned-warranty.pdf", FileTypes.Pdf)]
    [InlineData("receipt.png", FileTypes.Png)]
    [InlineData("receipt.webp", FileTypes.Webp)]
    public void TheType_ComesFromTheFirstBytes(string sample, string expected)
    {
        Assert.Equal(expected, FileTypes.Detect(File.ReadAllBytes(SampleFile.PathOf(sample)).AsSpan(0, FileTypes.HeaderLength)));
    }

    [Fact]
    public void JpegAndHeic_AreKnown_AndOtherContentIsNot()
    {
        Assert.Equal(FileTypes.Jpeg, FileTypes.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F']));
        Assert.Equal(FileTypes.Heic, FileTypes.Detect([0, 0, 0, 0x18, .. "ftypheic"u8, 0, 0, 0, 0]));
        Assert.Equal(FileTypes.Heic, FileTypes.Detect([0, 0, 0, 0x1C, .. "ftypmif1"u8, 0, 0, 0, 0]));
        Assert.Null(FileTypes.Detect([0, 0, 0, 0x18, .. "ftypisom"u8, 0, 0, 0, 0]));
        Assert.Null(FileTypes.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8));
        Assert.Null(FileTypes.Detect("%PD"u8));
        Assert.Null(FileTypes.Detect([]));
    }

    [Fact]
    public void Tags_AreTrimmed_LowerCase_SplitOnCommas_WithoutRepeats()
    {
        var tags = DocumentTags.Normalize(["  Kitchen ", "home,  Small   Appliances", "", null, "KITCHEN"]);

        Assert.Equal(new[] { "kitchen", "home", "small appliances" }, tags);
        Assert.Equal("|kitchen|home|small appliances|", DocumentTags.Store(tags));
        Assert.Equal(tags, DocumentTags.Read(DocumentTags.Store(tags)));
        Assert.Equal(string.Empty, DocumentTags.Store([]));
        Assert.Empty(DocumentTags.Read(string.Empty));
    }

    [Fact]
    public void ALongTag_IsRefused()
    {
        var error = Assert.Throws<InvalidInputException>(() => DocumentTags.Normalize([new string('a', 51)]));
        Assert.Equal("tags", error.Field);
        Assert.Single(DocumentTags.Normalize([new string('a', 50)]));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Scans\\receipt.pdf", null, "receipt.pdf")]
    [InlineData("../../etc/passwd", null, "passwd")]
    [InlineData("scan\u0007\u0000 1.pdf", null, "scan 1.pdf")]
    [InlineData("photo", FileTypes.Heic, "photo.heic")]
    [InlineData("", FileTypes.Pdf, "file.pdf")]
    [InlineData(null, null, "file")]
    [InlineData("..", FileTypes.Png, "file.png")]
    public void FileNames_LoseFoldersAndControlCharacters(string? name, string? contentType, string expected)
    {
        Assert.Equal(expected, DocumentService.CleanFileName(name, contentType));
    }

    [Fact]
    public void ALongFileName_KeepsItsExtension()
    {
        var name = DocumentService.CleanFileName(new string('x', 300) + ".pdf", FileTypes.Pdf);

        Assert.Equal(DocumentFile.MaxFileNameLength, name.Length);
        Assert.EndsWith("x.pdf", name);
    }

    [Fact]
    public void ALongFileName_IsNotCutInsideAnEmoji()
    {
        // The cut falls between the two halves of the emoji: half of one is not valid text.
        var cut = DocumentFile.MaxFileNameLength - ".pdf".Length;
        var name = DocumentService.CleanFileName(new string('x', cut - 1) + "😀 more.pdf", FileTypes.Pdf);

        Assert.Equal(new string('x', cut - 1) + ".pdf", name);
    }
}
