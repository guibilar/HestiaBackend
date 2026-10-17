using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application;

/// <summary>A file that <see cref="DocumentStorage.StoreAsync"/> stored.</summary>
/// <param name="Key">The storage key: <c>ab/abcdef…</c>, from the SHA-256 of the content.</param>
/// <param name="Sha256">The SHA-256 of the content, in lower-case hexadecimal.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="ContentType">The type from the content (<see cref="FileTypes.Detect"/>), or null for an unknown type.</param>
/// <param name="Created">True when this call wrote the file; false when the same content was stored already.</param>
public sealed record StoredFile(string Key, string Sha256, long Size, string? ContentType, bool Created);

/// <summary>The content is larger than the limit.</summary>
public sealed class FileTooLargeException(long limit) : Exception($"The file is larger than {limit} bytes.");

/// <summary>
/// The files of the documents, on a volume (D9). Each file is at <c>&lt;storage&gt;/ab/abcdef…</c>: the folder is the
/// first two characters of the SHA-256 of the content, the name is the whole hash. So the same content is stored once,
/// and a stored file never changes. A file that is written is first in <c>tmp/</c>, so a stopped upload never looks
/// like a stored file.
/// </summary>
public sealed partial class DocumentStorage(IOptions<DocumentsOptions> options)
{
    public const string TempFolder = "tmp";

    /// <summary>The folder of the thumbnails (T61): <c>thumbs/ab/abcdef….jpg</c>, by the storage key of their file.</summary>
    public const string ThumbnailFolder = "thumbs";

    private const int BufferSize = 81920;

    private static readonly Lock MoveLock = new();

    [GeneratedRegex("^[0-9a-f]{2}/[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormat();

    /// <summary>The full path of the storage folder.</summary>
    public string Root => options.Value.StoragePath;

    /// <summary>
    /// Stores the content. It reads at most <paramref name="maxSize"/> bytes: a larger content throws
    /// <see cref="FileTooLargeException"/>, and nothing is stored.
    /// </summary>
    public async Task<StoredFile> StoreAsync(Stream content, long maxSize, CancellationToken cancellationToken)
    {
        var tempFolder = Path.Combine(Root, TempFolder);
        Directory.CreateDirectory(tempFolder);
        var temp = Path.Combine(tempFolder, $"{Guid.NewGuid():N}.partial");
        var header = new byte[FileTypes.HeaderLength];
        var headerLength = 0;
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                             FileOptions.Asynchronous))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxSize)
                    {
                        throw new FileTooLargeException(maxSize);
                    }
                    if (headerLength < header.Length)
                    {
                        var count = Math.Min(read, header.Length - headerLength);
                        buffer.AsSpan(0, count).CopyTo(header.AsSpan(headerLength));
                        headerLength += count;
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            var key = $"{sha256[..2]}/{sha256}";
            var path = PathOf(key);
            var created = false;
            // One check and move at a time: of two uploads of the same content at the same time, only one writes the file,
            // so only that one removes it when its upload fails. (File.Move without overwrite is not atomic on every file
            // system; the documents volume has one host.)
            lock (MoveLock)
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Move(temp, path, overwrite: true);
                    created = true;
                }
                else
                {
                    Touch(path);
                }
            }
            return new StoredFile(key, sha256, size, FileTypes.Detect(header.AsSpan(0, headerLength)), created);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            File.Delete(temp);
        }
    }

    /// <summary>True when the text is a storage key: <c>ab/abcdef…</c> (lower-case hexadecimal).</summary>
    public static bool IsKey(string key) => KeyFormat().IsMatch(key);

    /// <summary>
    /// A new upload of content that is stored already uses the old file. A new time keeps it safe from
    /// <c>documents-clean-files</c> (it removes only old files) until the rows of the upload are saved.
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only a protection against a clean-up at the same time.
        }
    }

    /// <summary>The full path of a stored file.</summary>
    /// <exception cref="ArgumentException">The key is not a storage key.</exception>
    public string PathOf(string key)
    {
        if (!IsKey(key))
        {
            throw new ArgumentException($"\"{key}\" is not a storage key.", nameof(key));
        }
        return Path.Combine(Root, key[..2], key[3..]);
    }

    public bool Exists(string key) => File.Exists(PathOf(key));

    /// <summary>The full path of the thumbnail of a stored file (it can be missing).</summary>
    /// <exception cref="ArgumentException">The key is not a storage key.</exception>
    public string ThumbnailPathOf(string key)
    {
        if (!IsKey(key))
        {
            throw new ArgumentException($"\"{key}\" is not a storage key.", nameof(key));
        }
        return Path.Combine(Root, ThumbnailFolder, key[..2], key[3..] + ".jpg");
    }

    /// <summary>The storage key of a thumbnail path relative to the root (<c>thumbs/ab/abc….jpg</c>), or null.</summary>
    public static string? KeyOfThumbnail(string relativePath)
    {
        const string prefix = ThumbnailFolder + "/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal) || !relativePath.EndsWith(".jpg", StringComparison.Ordinal))
        {
            return null;
        }
        var key = relativePath[prefix.Length..^4];
        return IsKey(key) ? key : null;
    }

    /// <summary>Deletes a stored file and its thumbnail. A file that is not there is no error.</summary>
    public void Delete(string key)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        var thumbnail = ThumbnailPathOf(key);
        if (File.Exists(thumbnail))
        {
            File.Delete(thumbnail);
        }
    }
}
